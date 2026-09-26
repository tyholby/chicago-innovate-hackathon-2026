# Rhino integration

`rhino/` is a Rhino 8 plug-in scaffold: one command and one docked panel, drawn in the Family Studio
design language. It does not run the pipeline yet. User-facing instructions are in
[rhino/README.md](../rhino/README.md).

## Build

`dotnet build rhino/src/FamilyStudio.Rhino -c Release` builds `FamilyStudio.rhp` twice:

| Target | Loaded by |
| --- | --- |
| `net7.0` | Rhino 8 by default (8.20 and later run it on .NET 8) |
| `net48` | Rhino 8 started with `/netfx` |

- RhinoCommon `8.0.23304.9001` (`RhinoCommonVersion` in `rhino/Directory.Build.props`) is a compile-only
  reference (`ExcludeAssets=runtime`); it brings `RhinoCommon.dll`, `Rhino.UI.dll` and `Eto.dll`. Its
  version is also the oldest Rhino 8 the plug-in claims to support, so raise it only on purpose.
- `System.Drawing.Common` 7.0.0 (net7.0 only) draws the panel icon. The net48 lane uses the framework's
  `System.Drawing` and `Microsoft.NETFramework.ReferenceAssemblies`, so it also compiles on a Mac.
- `ImplicitUsings` is off, so every file lists its usings, and `LangVersion` is 12. Code must compile
  for both targets: no .NET 5+ only APIs outside `#if NET` blocks.
- `rhino/.env` is copied next to the `.rhp`.

## Identity

| What | Value | Where |
| --- | --- | --- |
| Plug-in ID | `d668aadc-d98b-4748-b2d4-c0e3664648bf` | `PlugInGuid` in the `.csproj`, emitted as the assembly `Guid` attribute |
| Panel ID | `6a645485-1efa-4764-a43e-4579affec9d1` | `[Guid]` on `HelloPanel` |
| Command | `FamilyStudio` | `FamilyStudioCommand.EnglishName` |
| Yak package | `FamilyStudio` | `rhino/manifest.yml` (keep `version` in step with `Directory.Build.props`) |

Never change the IDs: Rhino stores settings, panel layout and load state under them.

## Loading

- **Development:** `rhino\scripts\run.ps1 [-Configuration Debug|Release] [-Runtime netcore|netfx]
  [-NoBuild]` builds, sets `RHINO_PACKAGE_DIRS` to `bin\<Configuration>` for that Rhino process only,
  and starts `Rhino 8\System\Rhino.exe /nosplash /<runtime>`. Rhino picks the matching target folder
  itself. Nothing is installed.
- **Local install:** `rhino\scripts\install.ps1` packs the Release `net7.0` output with `manifest.yml`
  using Rhino's own `Yak.exe` (`yak build --platform win`) and installs the `.yak` file locally.
  `-Uninstall` removes it. Nothing is published.
- Loading the same plug-in from both places fails with "Id already in use".

## Code

| File | What |
| --- | --- |
| `FamilyStudioPlugin.cs` | `PlugInLoadTime.AtStartup`; registers the panel in `OnLoad` (a panel failure never blocks loading) |
| `FamilyStudioCommand.cs` | Toggles the panel |
| `HelloPanel.cs` | Eto `Panel` with one `Drawable`: a cover sheet and a title block with setup status. Double-click re-reads settings; `RhinoApp.AppSettingsChanged` re-reads the theme |
| `Theme.cs` | Paper and Night tokens and type roles, the same values as the Revit window. `HostUtils.RunningInDarkMode` picks one |
| `EnvStatus.cs` | Reads `.env` beside the plug-in, then `%APPDATA%\FamilyStudio\.env`; reports presence only. Signed in means `auth.json` exists in the Codex home (`%LOCALAPPDATA%\FamilyStudio\codex-home` unless `OPENAI_CODEX_HOME` is set), the same folder the Revit add-in uses |
| `PanelIcon.cs` | The Family Studio mark, drawn in code |

## Bringing the pipeline to Rhino

Not started. Things to know first:

- `FamilyStudio.Core` targets `net8.0` only and uses .NET 8 APIs (for example System.Text.Json's
  `JsonUnmappedMemberHandling`). It cannot load into Rhino's `net48` runtime, and needs Rhino 8.20 or
  later (which runs on .NET 8) on the `net7.0` lane. Decide that runtime floor before sharing the core.
- Everything Revit-specific sits behind `IStudioHost`; a Rhino host would build blocks or meshes from
  the same recipes and capture views from Rhino's viewports.
- Rhino's document and UI objects belong to Rhino's UI thread; marshal with `RhinoApp.InvokeOnUiThread`
  the way the Revit add-in uses `RevitDispatcher`.
