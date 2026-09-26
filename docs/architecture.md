# Architecture

How the Revit plug-in is put together. For the AI stages themselves see [pipeline.md](pipeline.md);
for the Codex protocol see [codex-integration.md](codex-integration.md); for Revit API details see
[revit-integration.md](revit-integration.md).

## Components

```
 Revit UI thread                         background threads                     Revit API context
 ---------------                         ------------------                     -----------------
 StudioWindow (WPF, XAML)
   |  bindings
 StudioViewModel ---- calls ----> StudioSession (Core/Pipeline)
   ^                                |        |
   | Changed / StateChanged         |        +--> IStudioHost ------------------> RevitStudioHost (Native)
   | (marshalled to Dispatcher)     |              (queued work)                   RevitDispatcher -> ExternalEvent
   |                                v                                               FamilyBuilder, Units
 CodexService (Core/Codex) <---- IStudioAgent
   |  JSON-RPC over stdio (JsonRpcConnection)
 CodexProcess x2 ("Reasoning", "Image") -> codex app-server (child processes)
```

| Type | File | Responsibility |
| --- | --- | --- |
| `App`, `OpenStudioCommand` | `FamilyStudio.Revit/App.cs` | Ribbon tab and button; forwards Revit UI theme changes |
| `StudioLauncher` | `FamilyStudio.Revit/StudioLauncher.cs` | One window per Revit session; builds the object graph; shutdown |
| `StudioWindow` | `UI/StudioWindow.xaml(.cs)` | The sheet layout, custom title bar, photo drag and drop, close guard |
| `StudioViewModel` | `UI/StudioViewModel.cs` | Inputs to drafts, commands, primary action, mirrors session and account state |
| `ThemeManager` | `UI/ThemeManager.cs` | Swaps `Paper.xaml` / `Night.xaml` at merged dictionary index 0 |
| `StudioSession` | `Core/Pipeline/StudioSession.cs` | The design state machine; runs one operation at a time |
| `CandidateLoop` | `Core/Pipeline/CandidateLoop.cs` | Stage, validate, ask for corrections (patches pinned to hashes) |
| `CodexService` | `Core/Codex/CodexService.cs` | Sign-in, model catalog, isolated app-server processes, stage runs |
| `CodexProcess` | `Core/Codex/CodexProcess.cs` | Launch arguments, feature detection, config verification |
| `JsonRpcConnection` | `Core/Codex/JsonRpcConnection.cs` | Newline-delimited JSON-RPC over stdio |
| `RevitStudioHost` | `Native/RevitStudioHost.cs` | Preview room, build, capture, load into projects |
| `RevitDispatcher` | `Native/RevitDispatcher.cs` | Awaitable queue onto Revit's API thread via `ExternalEvent` |
| `FamilyBuilder` | `Native/FamilyBuilder.cs` | Recipe to `.rfa`, with measure-back verification |
| `StudioEnvironment` | `Core/Config/StudioEnvironment.cs` | Settings from environment variables and `.env` files |
| `SessionJournal` | `Core/Pipeline/SessionJournal.cs` | Session folder: `events.jsonl` log and named artifacts |

The core never references Revit. Everything Revit-specific sits behind `IStudioHost`
(`Core/Pipeline/IStudioHost.cs`), which is why the probe and the tests can run the whole pipeline with
in-memory hosts.

## Lifecycle

1. Revit starts and calls `App.OnStartup`: the **Family Studio** tab and button are created, and Revit's
   `ThemeChanged` event is forwarded as `App.UiThemeChanged`.
2. The button runs `OpenStudioCommand`, which calls `StudioLauncher.Open`. If the window is open it is
   brought forward. Otherwise the launcher:
   - loads settings (`StudioEnvironment.Load`, from the add-in folder);
   - creates a session folder (`SessionJournal.CreateUnder`);
   - creates `CodexService`, `RevitStudioHost` (this creates the `ExternalEvent`, so it must happen in
     the command's API context), `StudioSession` and `StudioViewModel`;
   - shows the window modeless, owned by Revit's main window;
   - starts `StudioViewModel.StartAsync` inside a dispatcher operation, so every await in the view
     model resumes on the UI thread.
3. `StartAsync` finds Codex, starts the reasoning process, reads the ChatGPT account and the model
   catalog. The window shows the cover sheet until the user is signed in.
4. Closing the window cancels the current step and disposes `CodexService` at once, which kills both
   app-server processes. `StudioSession.StopAsync` then waits for queued Revit work to settle and
   releases the host.
5. `App.OnShutdown` closes the window without asking and releases the host right there, because Revit
   runs no external events while it shuts down. If Revit crashes instead, each app-server still exits:
   `codex app-server` stops when its standard input closes.

## Threading

| Thread | Runs | Rules |
| --- | --- | --- |
| Revit UI thread (WPF dispatcher) | Window, view model, bindings | The only thread that touches WPF objects and bound collections |
| Thread pool | Session operations after `ConfigureAwait(false)`, JSON-RPC reads, Codex events | Never touch WPF or Revit objects; raise events and let listeners marshal |
| Revit API context | `RevitDispatcher.Execute`, one `ExternalEvent` at a time | The only place Revit API calls happen |

`StudioSession.Changed` and `CodexService.StateChanged` can fire on any thread, and inside Revit's own
events. `StudioViewModel.OnBackgroundChange` never refreshes inline: it queues one dispatcher operation
(a burst of events costs one refresh), so every refresh runs with the UI thread's synchronization
context and after the session has finished its transition. Each refresh ends with
`CommandManager.InvalidateRequerySuggested`, because WPF only re-asks commands after user input, and
lists (families, findings, activity) are rebuilt only when their content changes.

`RevitDispatcher.RunAsync` queues a work item and raises the event. Queued work can be cancelled until it
starts; started work always finishes, so a cancel can never leave a transaction half applied. If Revit
does not run the event within 90 seconds (usually because a modal dialog is open) the call fails with a
readable message. An `Idling` handler re-raises the event for work queued while another item was running.

## State machine

`StudioSession.State` (`Core/Pipeline/StudioSession.cs`):

```
Draft --Generate--> PreparingBrief --> GeneratingReference --> Review
Review --Accept--> (accepted, still Review) --Build--> Building --> Built
Built --Review--> Checking <--> Repairing --> Ready | Built
Built (single item) --Revise--> Building --> Built
Built (single item) --Edit dimensions and finishes--> Review (not accepted)
any operation --> LimitReached | SignInRequired | TimedOut | Cancelled | DocumentUnavailable | Error
```

- One operation at a time (`RunOperation`); `IsBusy` is true while it runs, and `Cancel()` cancels it.
- Changing the inputs after a reference was made (before acceptance) calls `InvalidateDraft`, which
  returns to `Draft`. `Accept` refuses if the inputs differ from the ones the reference was made from.
- Acceptance freezes the brief and hashes it together with the reference image (`AcceptedDesign`); every
  build step verifies the image hash again.
- A failed or cancelled build keeps finished recipes (`_planned`), so building again resumes.
- Closing the preview room document moves the session to `DocumentUnavailable`; building again opens a
  fresh room and reuses the validated recipes and layout, so no AI planning runs again.

The view model's primary button follows the session, not the state name: sign in, then *Make reference*
(or *Read the photo*), *Accept design*, *Build family*, then *Load into project*.

## Session folder

`%LOCALAPPDATA%\FamilyStudio\sessions\<yyyy-MM-dd>\<HHmmss>-<id>\` (override with
`FAMILY_STUDIO_OUTPUT_DIR`). One folder per window opening.

| File | Written by |
| --- | --- |
| `events.jsonl` | Everything: one JSON object per line with `time`, `kind`, `data` |
| `draft-*.json`, `draft-archived-*.json` | The inputs of each Generate, and drafts replaced by New design |
| `reference-upload-*.png/jpg`, `reference-generated-<stage>.png/jpg` | The reference image |
| `brief.json`, `brief-reviewed.json` | The brief from the model, and after the user's review edits |
| `accepted-<timestamp>.json` | The accepted design (brief, hash, reference, draft); reopenable |
| `<stage>-prompt.txt` | Prompts of recipe, layout, repair and refine stages |
| `<stage>-<id>-output.txt` | Every model answer, also saved when a stage fails |
| `<stage>-accepted.json`, `placement-intents.json`, `review-<id>.json` | Validated results |
| `families/preview-N/<assetId>/revision-NN/<Family name>.rfa` | Every family revision, per preview room |
| `preview-rooms/Family Studio preview N.rvt` | The preview room of each accepted design |
| `captures/NN/<view>/*.png` | Plan, front-left and front-right exports |

## Settings

`StudioEnvironment` resolves each key from, in order: the process environment, `.env` beside the add-in
DLL (the build copies `revit/.env` there), then `%APPDATA%\FamilyStudio\.env`. Keys and defaults are in
[`revit/.env.example`](../revit/.env.example). None of them is a secret: the ChatGPT sign-in lives in
the Codex home, managed by Codex.
