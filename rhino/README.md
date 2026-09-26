# Family Studio for Rhino

A Rhino 8 plug-in scaffold. For now it does one thing: the `FamilyStudio` command opens a docked
**hello-world panel** drawn in the Family Studio design language (Paper in light mode, Night
drafting in dark mode), with a title block that shows whether the `.env` file, the ChatGPT sign-in
and the OpenRouter key are in place.

## Requirements

- Windows 10 or 11 with **Rhino 8** (any service release).
- **.NET SDK 8** or later ([download](https://dotnet.microsoft.com/download)).

## Run it while developing

From a PowerShell prompt in this `rhino` folder:

```powershell
copy .env.example .env      # optional
.\scripts\run.ps1
```

`run.ps1` builds the plug-in and starts Rhino 8 with `RHINO_PACKAGE_DIRS` pointing at the build
folder, the same mechanism McNeel's own templates use. Rhino loads `FamilyStudio.rhp` from there at
startup; nothing is installed, copied or written to the registry. In Rhino, run the command
**FamilyStudio**. Close Rhino, change code, run the script again.

`-Runtime netfx` starts Rhino on .NET Framework instead of .NET (the plug-in builds both).

## Install it

```powershell
.\scripts\install.ps1               # build, pack with Rhino's Yak tool, install locally
.\scripts\install.ps1 -Uninstall
```

This packs a Release build as a local Yak package and installs it into
`%APPDATA%\McNeel\Rhinoceros\packages\8.0`, so Rhino loads it every time it starts. Nothing is
published to the Rhino package server.

You can also drag `src\FamilyStudio.Rhino\bin\Debug\net7.0\FamilyStudio.rhp` onto a Rhino window,
or use **Options > Plug-ins > Install**. Rhino remembers the file from then on.

## Settings

`rhino/.env` uses the same keys as the Revit plug-in; see [.env.example](.env.example). The panel
only reports what is configured, it never shows values. Both plug-ins share Family Studio's own
Codex sign-in folder (`%LOCALAPPDATA%\FamilyStudio\codex-home`), so signing in with ChatGPT once in
Revit also counts here. Double-click the panel to re-read the settings.

## Layout

```
rhino/
  src/FamilyStudio.Rhino/
    FamilyStudioPlugin.cs    registers the panel when Rhino starts
    FamilyStudioCommand.cs   the FamilyStudio command: show or hide the panel
    HelloPanel.cs            the cover sheet, drawn on one Eto canvas
    Theme.cs                 design tokens and type roles, shared with the Revit window
    EnvStatus.cs             reads .env and reports what is configured
    PanelIcon.cs             the Family Studio mark as the panel icon
  scripts/                   build, run, install
  manifest.yml               the local Yak package manifest
```

The project targets `net7.0` (what Rhino 8 loads by default; 8.20 and later run it on .NET 8) and
`net48` (Rhino started with `/netfx`), and references RhinoCommon 8.0 for compilation only, so it
loads on every Rhino 8 release.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| "Id already in use" | The plug-in is loaded twice (the package and a loose build). Use one: uninstall the package, or stop loading the build folder |
| The build cannot overwrite `FamilyStudio.rhp` | Close Rhino first |
| The panel does not open | Run `FamilyStudio`, or open it from **Window > Panels** |
