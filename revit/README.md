# Family Studio for Revit

Words or a photo in, a native Revit family out.

Describe a piece of furniture, or drop in a product photo, and Family Studio drafts a reference
image, checks the dimensions and finishes with you, then builds a real Revit Furniture family:
native solids, named materials and one type, ready to load into any open project. It can also do
a coordinated collection of seven items, arranged in a preview room.

It runs on your ChatGPT plan: the plugin signs in with ChatGPT through OpenAI's Codex app. There is
no API key to buy or paste.

## Requirements

- Windows 10 or 11 with **Revit 2025, 2026 or 2027**.
- **.NET SDK 8** for Revit 2025 and 2026, or **.NET SDK 10** for Revit 2027
  ([download](https://dotnet.microsoft.com/download)).
- **Codex** on the same computer: the Codex desktop app, or the CLI (`npm install -g @openai/codex`).
- A **ChatGPT plan that includes Codex**.

## Set up

From a PowerShell prompt in this `revit` folder:

```powershell
copy .env.example .env      # optional: every setting has a default
.\scripts\install.ps1       # builds for Revit 2026 and registers the add-in
```

Use `-RevitYear 2025` or `-RevitYear 2027` for other versions, and `-Launch` to start Revit when it
is done. If PowerShell refuses to run scripts, run this once first:
`Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`.

Open Revit. A **Family Studio** tab appears on the ribbon. Click **Family Studio**, then
**Sign in with ChatGPT**: your browser opens, you sign in with OpenAI, and the window continues.
If the browser sign-in cannot reach this computer, choose **Use a code instead**.

`install.ps1` writes `%APPDATA%\Autodesk\Revit\Addins\<year>\FamilyStudio.addin`, pointing at the
add-in in this checkout's build folder. Nothing is copied into Revit's program folders and no
administrator rights are needed. `.\scripts\uninstall.ps1` removes the registration.

## Using it

1. **Brief.** The window opens on a complete example, a walnut lounge chair with its photo,
   name, size and finishes, so the whole flow can be tried straight away. For your own piece, click
   **New design** for a blank sheet (or edit the example) and/or drop a product photo on the plate.
   Add overall dimensions if you know them (650, 25.5 in or 2'-6" all work), or leave them blank and
   Family Studio proposes a size. Finishes are optional, one per line.
   For a room, choose *Collection of seven*, pick a starting point (office, bedroom suite, kitchen,
   treatment bay) or start blank, and edit the room description, the seven items and four finishes.
2. Click **Make reference** (or **Read the photo**). Family Studio writes a dimensioned brief and,
   without a photo, draws a reference image.
3. **Reference.** Check the name, dimensions and finishes; edit anything. Switch the stamp to
   **Dimensions verified** only when the sizes come from a spec sheet or a tape measure: verified
   sizes must match the built family within 2 mm on every axis. Click **Accept design**.
4. **Build.** Choose *Concept* or *Refined* detail and click **Build family**. Family Studio
   plans the geometry, validates it, and builds it in a separate preview room, then shows native
   plan and 3D views. **Review** compares those views with the reference and repairs what does not
   match (up to two passes). For a single item, **Revise** takes a sentence ("lower the back by
   50 mm") and rebuilds.
5. Pick an open project under **Load into project** and click **Load into project**, or
   **Save .rfa**. Place it with Revit's Component tool.

Every step can be cancelled; finished work is always kept. Ctrl+Enter runs the main action.
The window follows Revit's light or dark theme.

### What gets sent where

Your descriptions, photos and the preview-room views are sent to OpenAI, through Codex, under your
ChatGPT account. Everything Family Studio produces stays on this computer, in a session folder under
`%LOCALAPPDATA%\FamilyStudio\sessions`: the brief, the reference image, every family revision (.rfa),
the preview room (.rvt), view captures and an `events.jsonl` log. **Open session folder** in the
settings menu goes straight there.

## Settings

All settings live in `revit/.env` (copied next to the add-in at build time) or
`%APPDATA%\FamilyStudio\.env`. See [.env.example](.env.example) for every key.

- **ChatGPT through Codex** needs no key. The sign-in is stored and refreshed by Codex in
  `%LOCALAPPDATA%\FamilyStudio\codex-home`, a folder that belongs to Family Studio alone. To reuse
  the sign-in of your Codex app instead, set `OPENAI_CODEX_HOME=%USERPROFILE%\.codex`.
- `OPENAI_CODEX_PATH` points at `codex.exe` if it is somewhere unusual.
- `OPENAI_CODEX_MODEL` and `OPENAI_CODEX_REASONING_EFFORT` choose the defaults; the settings menu
  (click your account name) lists every model your plan offers.

## How it works

```
revit/
  src/FamilyStudio.Core/     plain .NET 8, no Revit: model, validation, prompts, Codex client, pipeline
  src/FamilyStudio.Revit/    the add-in: ribbon, WPF window, family builder, preview room
  tests/                     unit tests for the core (run anywhere)
  tools/FamilyStudio.Probe/  the pipeline on the command line, without Revit
  scripts/                   build, install, uninstall
```

Each AI step is a fresh Codex thread with one message and a strict JSON schema for its answer:

1. **Brief**: the input becomes a structured brief in metres (items, sizes, sized components,
   materials with RGB colours, support).
2. **Reference**: Codex draws a reference image (skipped when you supply a photo).
3. **Recipe**, per item: the geometry as named parts in real shapes: boxes with rounded edges,
   tapered and splayed cylinders, spheres, bent tubes and extruded profiles with rounded corners, with
   symmetric parts mirrored. Family Studio measures the exact geometry itself and validates size (max
   10 mm or 5 percent per axis, or 2 mm when verified), floor contact, materials and sized components. A rejected recipe
   goes back with the exact failed fields, and the model returns a patch pinned to the rejected
   version's hash, up to two times.
4. **Layout**, for collections: placement *intents* (absolute, relative to another item, on a named
   surface of another item, mirrored, facing something). Family Studio resolves them into positions,
   computes support heights, and checks room bounds, floor contact and footprints.
5. **Build**: Revit builds each family from its recipe (`FreeFormElement` solids from extrusions and
   revolutions, one material per finish, one type named by its size), measures it back, loads it into the preview
   room and places it. Revit work runs through an `ExternalEvent` queue and never holds a
   transaction open while waiting for AI.
6. **Review and repair** (optional): a fresh reviewer compares the plan and two 3D captures with the
   reference and the measured model; repairs are targeted patches, validated before any Revit change.

**Codex isolation.** Family Studio starts its own hidden `codex app-server` processes with
everything it does not need switched off: no shell, file edits, browser, web search, plugins, apps,
hooks, memories or MCP servers, approvals set to never and a read-only sandbox. Reasoning steps run
in a process that cannot generate images at all. It checks the effective configuration before use,
only disables features the installed Codex version knows (an unknown flag would stop Codex from
starting), and stops any step in which the model tries to use a tool.

## Development

The core and its tests are plain .NET 8, so they build and run on Windows, macOS or Linux:

```bash
dotnet test tests/FamilyStudio.Core.Tests
```

The add-in builds anywhere too: without a local Revit it compiles against reference-only Revit API
packages (`dotnet build src/FamilyStudio.Revit -p:RevitYear=2026`). It only runs inside Revit.

The **probe** runs the real pipeline without Revit, including sign-in, and draws each family as an
isometric SVG, which is the fastest way to work on prompts:

```bash
dotnet run --project tools/FamilyStudio.Probe -- status
dotnet run --project tools/FamilyStudio.Probe -- signin
dotnet run --project tools/FamilyStudio.Probe -- single "A walnut lounge chair with charcoal wool cushions" --effort medium
dotnet run --project tools/FamilyStudio.Probe -- single "Side table" --size 500x500x550
dotnet run --project tools/FamilyStudio.Probe -- collection bedroom
```

Revit locks the add-in DLL while it is loaded, so close Revit before rebuilding. A new `.addin`
file is picked up by a running Revit, but a rebuilt DLL needs a restart.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| "Codex was not found" | Install the Codex app or `npm install -g @openai/codex`, or set `OPENAI_CODEX_PATH`, then choose **Try again** |
| The browser sign-in never returns | Use **Use a code instead** |
| "Your ChatGPT usage limit was reached" | The message shows when it resets. Lower the reasoning effort in settings to use less |
| "Furniture family template was not found" | Install Revit's family templates, or set `FAMILY_STUDIO_FURNITURE_TEMPLATE` to a furniture `.rft` |
| "Revit is busy" | Close any open Revit dialog; Family Studio waits for Revit to be idle |
| The build cannot overwrite `FamilyStudio.Revit.dll` | Close Revit first |
| No Family Studio tab | Check `%APPDATA%\Autodesk\Revit\Addins\<year>\FamilyStudio.addin` exists and points at a built DLL |
