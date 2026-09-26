# Status and decisions

Keep this file current. Add what you verified (with the date and versions) and any decision the team
makes, so the next person does not have to rediscover it.

## Status (2026-09-26)

### Verified

On macOS, with the .NET 8 and 10 SDKs:

- The add-in builds with zero warnings for Revit 2025, 2026 and 2027 (against the reference-only Revit
  API packages); the Rhino plug-in builds for `net7.0` and `net48`; the probe builds.
- 67 core unit tests pass.
- Every binding path in `StudioWindow.xaml` resolves to a view model property (static check).
- The PowerShell scripts parse (PowerShell 7.6).

Live, with Codex 0.154.0-alpha.6.2 (the build that ships with the ChatGPT app):

- Probe `status` with a new, empty Codex home: the reasoning process starts and passes its
  configuration check, the account reads as signed out, and the model catalog loads.
- Probe `single` with an existing Codex sign-in (`OPENAI_CODEX_HOME` pointing at it) on a ChatGPT plan
  that includes Codex: both processes, brief, reference image and recipe for a lounge chair (16 parts),
  and a side table with verified dimensions (500 x 500 x 550 mm) that passed validation on the first
  attempt.
- Sign-in bookkeeping, with a never-signed-in Codex home: a browser sign-in started, replaced by a
  second one and cancelled. Codex reports the replaced attempt as failed; that report is ignored and the
  second attempt stays open until cancelled.
- `codex app-server` exits as soon as its standard input closes.

### Not verified yet

- **Anything inside Revit or Rhino.** No Windows machine has run either plug-in. See the checklist at
  the end of [revit-integration.md](revit-integration.md).
- A completed in-app ChatGPT sign-in (browser and device code). Starting and cancelling one is
  verified; finishing one needs a person at the browser.
- A live collection run, and live review and repair (the probe builds without review).
- The Windows Codex install locations in `CodexExecutable`, and `codex.exe` launched from Revit.

### Open items

- The first Windows run of both plug-ins, with the checklist above.

### Runtime review (2026-09-26)

A read-through of the add-in for problems that only show up inside Revit. Fixed:

| Problem | Fix |
| --- | --- |
| Cancelling as an operation ended could throw `ObjectDisposedException` on Revit's UI thread | `StudioSession.Cancel` tolerates a finished operation |
| Loading a second design could replace the first design's family (same name) in the user's project | Loaded families are owned per preview room and item, by unique ID; anything else gets a free name |
| Buttons stayed disabled after work that finished without user input | Every refresh asks WPF to re-query commands |
| A refresh inside a Revit event could resume on a thread-pool thread and touch bound collections | The view model always refreshes in its own dispatcher operation, coalesced |
| Closing the preview room left the window offering **Load into project** for families that were gone | The session forgets the room before announcing it, keeps the recipes and layout, and waits for `DocumentClosed` (a close can be cancelled) |
| A reopened design lost its photo, and its sizes (shown to 0.1 mm) never matched the inputs again | The photo is restored without the edit guard; inputs match with sizes within 0.1 mm |
| Codex could keep running for 90 seconds after the window closed (and during Revit shutdown) | Codex is disposed with the window; on shutdown the host is released synchronously |
| The model catalog was only read before sign-in | It is read again after every sign-in |
| A replaced sign-in's failure could fail the new one or hide its device code | Completions are matched by `loginId`, and only the newest attempt updates the window |
| New instances could lose their height (level-based placement) | Instances are moved to their target in 3D, and placements are measured back |
| Deleting the template's last family type throws, which would fail every build | The new type is added before the others are deleted |
| Smaller: target-project errors were reported as a closed preview room; revisions from different rooms overwrote each other's files; the chosen mode segment looked disabled; 40 MP photos were decoded at full size; the theme was read outside Revit's API context | Each fixed where it happens |

Still to confirm on Windows: that `SaveAs` followed by `OpenAndActivateDocument` hands back the same
preview room document, and everything in the checklist in [revit-integration.md](revit-integration.md).

## Decisions

| Decision | Why |
| --- | --- |
| The AI runs only on the user's ChatGPT plan, through `codex app-server` | No API keys to buy or leak; Codex handles sign-in and token refresh |
| No other model providers | One provider keeps the pipeline and its prompts simple |
| No pasteable ChatGPT credential | The only token login in the protocol is marked internal to OpenAI |
| `OPENROUTER_API_KEY` is reserved | The settings slot and a key check exist; nothing is sent to OpenRouter yet |
| Family Studio has its own Codex home by default | A personal Codex setup (MCP servers, skills, config) never leaks into the plug-in; reusing it is opt-in with `OPENAI_CODEX_HOME` |
| Two Codex processes, and only known feature flags | Reasoning stages cannot generate images; unknown flags stop Codex from starting; image generation needs code mode |
| Scope: one item, or a collection of seven | Matches the two flows in the window; the limits live in `StudioLimits` |
| Geometry is boxes tilted about X, built as `FreeFormElement`s | Simple enough for the model to get right and for code to validate exactly |
| Every design builds in its own preview room | The user's model changes only when they choose **Load into project**, and their families are never overwritten |
| One family type, named by the accepted size | Rebuilt revisions reload in place without new types |
| No NuGet packages in the core or the add-in; C# 12 | Revit's shared load context, and identical builds on SDK 8 and 10 |
| No Revit add-in dependency isolation | Not needed without packages, and it complicates WPF resource loading |
| Per-user `.addin` manifest pointing at the build output; Rhino via `RHINO_PACKAGE_DIRS` or a local Yak package | Local installs only; nothing is published to any store |
| Paper and Night themes, system fonts only | Follows the host's light or dark UI with nothing to license or bundle; see [design-system.md](design-system.md) |
| MIT license | Public hackathon project |
