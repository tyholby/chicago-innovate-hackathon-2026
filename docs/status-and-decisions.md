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

On Windows 11 Pro ARM64 with Revit 2027.3 (x64 under emulation), .NET SDK 10.0.401 and Codex CLI 0.157.1,
following [local-install.md](local-install.md):

- `install.ps1 -RevitYear 2027` builds against the local Revit API with 2 MSB3277 warnings
  (`Microsoft.VisualBasic` and `System.Drawing` version conflicts from Revit's own assemblies) and
  registers the manifest.
- After the unsigned add-in prompt, Revit loads the add-in: `Starting External Application: Family Studio`
  succeeds, the **Family Studio** tab gets its **Design** panel and button, and `ThemeChanged` is
  registered.
- The winget Codex CLI (`OpenAI.Codex`) runs (`codex-cli 0.157.1`), but it installs no `codex.exe`, so
  Family Studio needs `OPENAI_CODEX_PATH`. Launched that way from Revit, Codex starts for every window.
- **Sign in with ChatGPT** from the window, through the browser, with Family Studio's own Codex home.
- A complete single item in Revit, on the example chair as the window opens (photo, name, size and
  finishes) at `xhigh` with `gpt-6-astra`: the photo read into a brief (42 s), acceptance, the preview
  room, a recipe accepted on the first attempt (3.4 minutes), the family (`Walnut Lounge Chair.rfa`), its
  placement, and the plan and both 3D captures.
- Curved shapes, once, in Revit (prompts version 2, Refined, `xhigh`), on a photo of a stone relief: the
  model used every shape (18 cylinders, 18 profiles, 17 tubes, 6 spheres, 1 box; 60 parts), the recipe
  passed validation on the first attempt and Revit built it. `shape_fallbacks` kept two drapery folds as
  11 and 9 overlapping solids because Revit could not unite them.
- `dotnet test` passes (114 tests, run on the .NET 10 runtime); the add-in builds for 2025 and 2026 with
  zero warnings and for 2027 with the 2 MSB3277 above; the probe and the Rhino plug-in build.

### Performance (2026-09-26)

Profiled from the session logs. Codex writes about 33 tokens a second, steadily, so a stage's time is its
output tokens: reasoning plus answer. Revit's own work (preview room, build, captures) takes seconds.

| Run | Brief | Recipe | Recipe tokens (reasoning + answer) |
| --- | --- | --- | --- |
| Example chair, boxes only, `xhigh` | 42 s | 3.4 min | 4,860 + 1,738 |
| Stone relief, curved shapes, Refined, `xhigh`, 60 parts | 42 s | 14.7 min | 15,020 + 14,198 |
| The same photo, prompts version 3, Concept, `xhigh`, 27 parts | 44 s | 7.5 min | 11,122 + 3,867 |

About a third of that relief answer was field names (every part listed all 15 fields) and 28 percent was
indentation. Prompts version 3 lists only each shape's fields and asks for compact JSON: rewritten that
way, the same answer is half the size (35.0k to 17.7k characters). Its first live run (the third row)
answered in one compact line, passed validation on the first attempt, and was about 40 percent smaller
per part. Reasoning at `xhigh` is most of the rest, which is why new windows now start at `low` (see
[codex-integration.md](codex-integration.md)).

### Not verified yet

- **Parallel planning, live.** A collection's parallel recipes run several Codex threads in one app-server
  process; confirm that on a first live collection, and watch for rate limits. Prompts version 3 is
  verified on one single item (above).
- **View2Render.** It builds for every Revit year and its core (prompt, reference images, progress) has
  unit tests, but it has not run in Revit or live: the capture of each view type, the render through
  Codex's image tool with and without reference images, progress, cancel and download. The checklist is
  in [revit-integration.md](revit-integration.md). The probe's `render` command runs the render stage
  without Revit.
- **The curved shapes in Revit beyond one run** (above): check the session log for `shape_fallbacks`.

- **The rest of the Revit checklist, and anything in Rhino.** In Revit, not yet run: review and repair,
  revise, **Save .rfa**, **Load into project**, collections, the Night theme and closing the preview room
  while a build is queued. No Windows machine has run the Rhino plug-in. See the checklist at the end of
  [revit-integration.md](revit-integration.md).
- A completed device-code sign-in (the browser sign-in is verified on Windows, above).
- A live collection run, and live review and repair (the probe builds without review).
- The Windows Codex install locations in `CodexExecutable`.

### Open items

- The rest of the first Windows run: the Revit checklist above, and the Rhino plug-in.
- `CodexExecutable` does not find the winget Codex CLI: there is no `codex.exe` on PATH, and its package
  folder (`%LOCALAPPDATA%\Microsoft\WinGet\Packages\OpenAI.Codex_*`, binary
  `codex-<arch>-pc-windows-msvc.exe`) is not a search location. Until it is, set `OPENAI_CODEX_PATH`.
- Template fallback: Revit 2027 keeps its imperial templates in `Family Templates\English-Imperial`, not
  `English_I`. `Metric Furniture.rft` in `English` is still found, so this only matters where the metric
  templates are missing.

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
| No other model providers or API keys | One provider keeps the pipeline, its prompts and its settings simple |
| No pasteable ChatGPT credential | The only token login in the protocol is marked internal to OpenAI |
| Family Studio has its own Codex home by default | A personal Codex setup (MCP servers, skills, config) never leaks into the plug-in; reusing it is opt-in with `OPENAI_CODEX_HOME` |
| Two Codex processes, and only known feature flags | Reasoning stages cannot generate images; unknown flags stop Codex from starting; image generation needs code mode |
| Scope: one item, or a collection of seven | Matches the two flows in the window; the limits live in `StudioLimits` |
| Geometry uses five shapes: boxes with rounded edges, cylinders and tapers on any axis, spheres, tubes and extruded profiles, with mirroring (2026-09-26) | Boxes alone made every item look blocky. Each shape still has exact bounds in code and is built only from extrusions and revolutions, so validation and the measure-back stay exact |
| A recipe part lists only its shape's fields, points are `[x, y, z]`, and answers are compact JSON with lengths to whole millimetres (prompts version 3) | Output tokens are most of a stage's time; a recorded 60-part answer halves |
| A collection plans up to four recipes at once, each in its own Codex thread | Seven recipes in a row took seven times one recipe; each is independent until the layout |
| View2Render renders through Codex image generation at `low` effort, with up to eight reference images (2026-09-26) | The same ChatGPT sign-in and no API key. Codex picks the image model (the protocol has no setting for it), and the reasoning step only hands the brief and images to the image tool |
| New windows start at `low` reasoning effort, not `xhigh` (2026-09-26) | A stage's time is its output tokens, and `xhigh` reasoning was most of them: an item took minutes. Higher efforts stay one click away in Settings, and `OPENAI_CODEX_REASONING_EFFORT` still sets a default |
| Every running stage shows live progress on the detail line | A 15-minute step behind a static status looks like a hang |
| Every design builds in its own preview room | The user's model changes only when they choose **Load into project**, and their families are never overwritten |
| One family type, named by the accepted size | Rebuilt revisions reload in place without new types |
| No NuGet packages in the core or the add-in; C# 12 | Revit's shared load context, and identical builds on SDK 8 and 10 |
| No Revit add-in dependency isolation | Not needed without packages, and it complicates WPF resource loading |
| Per-user `.addin` manifest pointing at the build output; Rhino via `RHINO_PACKAGE_DIRS` or a local Yak package | Local installs only; nothing is published to any store |
| Paper and Night themes, system fonts only | Follows the host's light or dark UI with nothing to license or bundle; see [design-system.md](design-system.md) |
| MIT license | Public hackathon project |
