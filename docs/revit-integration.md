# Revit integration

The add-in (`revit/src/FamilyStudio.Revit/`): how it builds for each Revit year, how Revit loads it,
and what it does inside Revit. The AI side is in [pipeline.md](pipeline.md).

## Versions and builds

| Revit | Target framework | SDK |
| --- | --- | --- |
| 2025 | `net8.0-windows` | .NET 8 or later |
| 2026 | `net8.0-windows` (the .5 update runs it on .NET 10) | .NET 8 or later |
| 2027 | `net10.0-windows` | .NET 10 |

One build per year: `dotnet build revit/src/FamilyStudio.Revit -c Release -p:RevitYear=2026`
(default 2026). Output goes to `src/FamilyStudio.Revit/bin/<Configuration>/<year>/`, and intermediate
files to `obj/<year>/` so the three years never share restore state. 2027 builds define
`REVIT2027_OR_GREATER`.

- **Revit API references.** With Revit installed (`C:\Program Files\Autodesk\Revit <year>\RevitAPI.dll`)
  the project references `RevitAPI.dll` and `RevitAPIUI.dll` from there with `Private=false`. Otherwise,
  or with `-p:UseRevitNuget=true`, it uses the reference-only `Nice3point.Revit.Api.*` packages for that
  year (`ExcludeAssets=runtime`). Neither is ever copied to the output.
- **Other operating systems.** `EnableWindowsTargeting` lets WPF compile on macOS and Linux, so the add-in
  builds anywhere. It only runs in Revit.
- **No other packages.** Revit 2025 and 2026 load add-ins into Revit's own load context, where
  assemblies Revit has already loaded win. The add-in and the core therefore reference no NuGet packages
  and use only what .NET 8 ships.
- **Settings.** `revit/.env`, when present, is copied next to the DLL at build time.

## Registration

`revit\scripts\install.ps1 [-RevitYear 2025|2026|2027] [-Configuration Release|Debug] [-NoBuild] [-Launch]`
builds (through `build.ps1`) and writes a per-user manifest,
`%APPDATA%\Autodesk\Revit\Addins\<year>\FamilyStudio.addin`:

| Field | Value |
| --- | --- |
| `Type` | `Application` |
| `Assembly` | Absolute path to `FamilyStudio.Revit.dll` in this checkout's build folder |
| `AddInId` | `016949e7-724a-4c46-973f-0899c23f1739` (never change it) |
| `FullClassName` | `FamilyStudio.Revit.App` |
| `VendorId` | `FamilyStudio` |

Nothing is copied into Revit's folders and no administrator rights are needed. Revit reads manifests at
startup and locks the DLL while it runs: close Revit before rebuilding. `uninstall.ps1` removes the
manifest. The manifest does not opt into Revit 2026's add-in dependency isolation: WPF resource
dictionaries loaded by pack URI are the first thing to test if that is ever turned on.

## Entry points

- `App.OnStartup` creates the **Family Studio** ribbon tab (tolerating an existing one), a **Design**
  panel and the **Family Studio** button (icon drawn in code by `RibbonIcon`), and subscribes to
  `ThemeChanged`. Only `ThemeType.UITheme` changes are forwarded, as `App.UiThemeChanged`.
- `OpenStudioCommand` (`TransactionMode.Manual`) calls `StudioLauncher.Open`. The command context is the
  only place the `ExternalEvent` behind `RevitDispatcher` can be created.
- `App.OnShutdown` closes the window immediately, which stops both Codex processes.
- `ThemeManager` reads `UIThemeManager.CurrentTheme` (Revit 2024 and later) and swaps `Paper.xaml` or
  `Night.xaml` into the window's merged dictionaries at index 0.

## Running Revit work

All Revit API calls go through `RevitDispatcher.RunAsync(Func<UIApplication, T>, CancellationToken)`,
which queues the work and raises one `ExternalEvent`; `Execute` drains the queue on Revit's API thread.

- Queued work can be cancelled until it starts; started work always finishes.
- Work that has not started within 90 seconds fails with "Revit is busy (a dialog may be open)".
- `ExternalEventRequest.Denied` fails the work immediately.
- An `Idling` handler raises the event again if work was queued while the handler was already running.
- `WhenIdle` completes when nothing is queued or running; `StudioSession.StopAsync` waits on it.

## The preview room

`RevitStudioHost` builds every accepted design in its own project document, never in the user's model.
A new room is made on the first build after each `BindDesign` (that is, after each acceptance); earlier
rooms stay open and on disk.

| Element | Details |
| --- | --- |
| Document | `NewProjectDocument(UnitSystem.Metric)`, saved as `preview-rooms/Family Studio preview N.rvt` in the session folder, then opened and activated |
| Level | "Family Studio floor" at 0 |
| Floor | 8 x 6 m (X -4..4, Y -3..3), first floor type found |
| Back wall | First basic wall type, along Y = 3.1 m, 2.8 m high |
| Plan | "Family Studio plan", 1:20 for a single item and 1:50 for a collection, annotations hidden, cropped to the item for a single item |
| 3D views | "Family Studio front-left" (eye -7, -8, 5 m) and "front-right" (eye 7, -8, 6 m), shaded with edges, fine detail, section box around the item (0.25 m margin) or the room |

The room tracks its own edits: `DocumentChanged` on the room increments a change stamp and drops stale
captures. Closing is a pair of events: `DocumentClosing` records the room's `DocumentId` (a close can
still be cancelled there), and `DocumentClosed` with that ID and status `Succeeded` marks the room
unavailable and raises `DocumentUnavailable`, which moves the session to `DocumentUnavailable`. Build
steps pass the stamp they expect (`NativeExpectation`), so work planned against an edited room is
refused.

## Building a family

`FamilyBuilder.Create`, run inside the dispatcher:

1. Validate the recipe again (`RecipeRules.Validate`).
2. Resolve the template: `FAMILY_STUDIO_FURNITURE_TEMPLATE` (must be an existing `.rft`), otherwise
   `Metric Furniture.rft` or `Furniture.rft` under Revit's `FamilyTemplatePath` or
   `%ProgramData%` / `%ProgramFiles%\Autodesk\RVT <version>\Family Templates\English` (or `English_I`).
3. In one transaction: category Furniture; add one type named from the accepted size
   (`"500 x 500 x 550 mm"`, so rebuilt revisions keep the type), then delete the template's other types
   (in that order: Revit refuses to delete a family's last type); set Description and Type Comments; for each part, extrude its rectangle, rotate it about X through its centre when tilted
   (`SolidUtils.CreateTransformed`), and add it as a `FreeFormElement`; one material per brief material
   (RGB colour, unique name).
4. Measure the solids back (bounding boxes of every element) and reject the family if any axis differs
   from the validated envelope by more than 2 mm, or from verified dimensions by more than 2 mm.
5. Save to `families/preview-N/<assetId>/revision-NN/<Family name>.rfa` and close the family document.
   Revisions are numbered per preview room, hence the room folder.

Family names come from the item names (`SafeName`: file-safe, at most 60 characters). Two items with the
same name get their item ID appended.

## Applying a build

`ApplyAsync(BuildProposal, NativeExpectation?)` makes sure the room exists, checks the expected change
stamp, then runs one `TransactionGroup` ("Family Studio: build"):

- for each recipe: build the `.rfa` (above), load it into the room with `OverwriteFamilyLoadOptions`
  (a revision reloads in place, instances keep their positions), activate its type;
- one "arrange" transaction: create missing instances with
  `NewFamilyInstance(point, symbol, level, NonStructural)`, move every instance to its target in 3D
  (the level-based overload can drop the height, which would put a lamp on the floor instead of the
  desk), then rotate about Z to the planned angle.

The group is assimilated on success and rolled back on any error or cancel. The session then measures
the placements back, as it does the families: every instance must be within 2 mm and 0.1 degrees of its
plan, or the build fails. The room is saved after
every build. `CaptureAsync` then exports the plan and both 3D views with `ExportImage` (PNG, 150 dpi,
1600 px wide, fit to page) into `captures/NN/<view>/`.

## Loading into a project

`ListProjectsAsync` offers every open project except family documents, linked models and the preview
room. `LoadFamiliesAsync` loads the chosen families in one transaction group. Each loaded family is
remembered by its unique ID together with the preview room and item it came from. Loading the same item
of the same design again reloads that family in place. Any other family with the same name (the user's
own, or one from an earlier design) is never overwritten: the `.rfa` is copied under the next free name
("Walnut Lounge Chair 2") and loaded as that. Problems with the chosen project are reported without
touching the session's preview room state.

## Not yet verified in Revit

Everything in this project compiles against the Revit 2025, 2026 and 2027 API, and the whole pipeline
runs outside Revit through the probe. The add-in itself has not yet been run inside Revit. On the first
Windows run, check in particular: the ribbon icon and the window in both Revit themes, sign-in from the
window, template discovery on non-English installs, the preview room and its views, image export file
names, loading into a project with a clashing family name, and closing the preview room while a build
is queued. Record what you find in [status-and-decisions.md](status-and-decisions.md).
