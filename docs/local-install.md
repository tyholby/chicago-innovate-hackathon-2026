# Local install on Windows

The exact steps to build, register and load the Revit add-in on a Windows machine, written for a coding
agent doing it for a person. [revit-integration.md](revit-integration.md) explains how the add-in builds
and loads; this file is the runbook, including the traps met on a real machine.

Verified end to end on 2026-09-26: Windows 11 Pro ARM64, Revit 2027.3 (x64, under emulation), .NET SDK
10.0.401, Codex CLI 0.157.1 and Git 2.55.0, with the agent running inside the Claude desktop app.

## Who does what

The agent can do everything except these, which belong to the person:

| Step | Why |
| --- | --- |
| Admin installs (Git, .NET SDK) in **Terminal (Admin)** | A UAC prompt raised from an agent's shell may never reach the screen (step 2) |
| Answering Revit's **Security - Unsigned Add-In** prompt | It is a trust decision |
| **Sign in with ChatGPT** in the Family Studio window | It is their account |
| Closing a Revit that has a document open | Unsaved work |
| Changing system settings (execution policy, `LongPathsEnabled`) | They are the person's settings |

## 1. Look before installing

```powershell
Get-ChildItem "$env:ProgramFiles\Autodesk\Revit *\Revit.exe" | ForEach-Object { "$($_.FullName)  $($_.VersionInfo.ProductVersion)" }
Get-Command git, dotnet -ErrorAction SilentlyContinue | Select-Object Name, Source
dotnet --list-sdks
Get-Process Revit -ErrorAction SilentlyContinue | Select-Object Id, MainWindowTitle
```

- The Revit year decides the SDK: .NET 10 for 2027, .NET 8 or later for 2025 and 2026.
- A Revit window title ending in `[Home]` means no document is open. Anything else: ask before closing it.
- Installers update PATH in the registry only. A shell that was already open, including the agent's own,
  keeps its old PATH, so start every command after an install with:

```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
```

## 2. Install what is missing

Use the community source (`--source winget`). It also keeps winget away from the Microsoft Store source
and its terms prompt.

| Need | Command | Admin |
| --- | --- | --- |
| Git | `winget install --id Git.Git -e --source winget` | Yes |
| .NET SDK 10 (SDK 8 is enough for Revit 2025 and 2026) | `winget install --id Microsoft.DotNet.SDK.10 -e --source winget` | Yes |
| Codex CLI | `winget install --id OpenAI.Codex -e --source winget --scope user` | No |

The GitHub CLI is not needed: the repository is public.

**Admin installs go to the person.** Both installers elevate themselves. Git's does so even with
`--scope user` when the account is an administrator (its Inno Setup script sets
`PrivilegesRequired=none`, which runs elevated for members of Administrators), and the .NET SDK has no
per-user winget installer. Started from the agent's background shell, the UAC prompt did not come to the
front (most likely it only flashed on the taskbar), and after about two minutes winget reported
`You cancelled the installation` (installer exit code 2). Do not retry from the agent. Ask the person to
right-click Start, open **Terminal (Admin)** and run the command, then poll for the result, for example
until `& "$env:ProgramFiles\dotnet\dotnet.exe" --list-sdks` lists a `10.` SDK.

**Codex needs a path.** winget printed `Command line alias added: "codex"` but created no `codex.exe`
anywhere. The only binary is `codex-aarch64-pc-windows-msvc.exe` (`x86_64` instead of `aarch64` on Intel
and AMD machines) in `%LOCALAPPDATA%\Microsoft\WinGet\Packages\OpenAI.Codex_Microsoft.Winget.Source_8wekyb3d8bbwe\`.
Family Studio looks for `codex.exe` ([codex-integration.md](codex-integration.md)), so it cannot find it
there. Check the binary with `--version` (expect `codex-cli 0.x.y`) and set `OPENAI_CODEX_PATH` in step 4.

## 3. Clone to a short path

```powershell
git clone https://github.com/tyholby/chicago-innovate-hackathon-2026.git "$env:USERPROFILE\workspace\chicago-innovate-hackathon-2026"
```

Windows keeps the 260-character path limit unless `LongPathsEnabled` is 1 (under
`HKLM\SYSTEM\CurrentControlSet\Control\FileSystem`). An agent's scratch workspace inside an app's data
folder can already be about 200 characters deep, and `git clone` there fails at the first file under
`.git\hooks` with `Filename too long`. Clone somewhere short instead.

## 4. Settings: `revit\.env`

```powershell
cd "$env:USERPROFILE\workspace\chicago-innovate-hackathon-2026\revit"
Copy-Item .env.example .env
```

Then set the Codex path. Values are expanded with `Environment.ExpandEnvironmentVariables`, which also
keeps personal paths out of the file:

```text
OPENAI_CODEX_PATH=%LOCALAPPDATA%\Microsoft\WinGet\Packages\OpenAI.Codex_Microsoft.Winget.Source_8wekyb3d8bbwe\codex-aarch64-pc-windows-msvc.exe
```

`.env` is ignored by git: never commit it. The build copies it next to the DLL, so edit it before
building, or rebuild after editing.

**Agents inside a packaged (MSIX) desktop app, such as the Claude desktop app: use `revit\.env`, not
`%APPDATA%\FamilyStudio\.env`.** The app's AppData virtualization applies to the agent's shell. A new
folder it creates under `%APPDATA%` or `%LOCALAPPDATA%` lands in the app's private copy
(`%LOCALAPPDATA%\Packages\<app package>\LocalCache\...`), which Revit never sees. A new file in a folder
that already exists outside the package, such as `%APPDATA%\Autodesk\Revit\Addins\<year>`, is written
for real, so the add-in manifest is fine. When unsure, write a probe file and look for it under
`LocalCache`.

## 5. Build and register

```powershell
.\scripts\install.ps1 -RevitYear 2027
```

- Expect `Build succeeded`, then `Registered: ...\Autodesk\Revit\Addins\2027\FamilyStudio.addin`
  pointing at `revit\src\FamilyStudio.Revit\bin\Release\2027\FamilyStudio.Revit.dll`.
- Against a local Revit 2027.3 install the build reports 2 warnings, both MSB3277 (`Microsoft.VisualBasic`
  10.0 and 10.1, `System.Drawing` 4.0 and 10.0). They come from Revit's own API assemblies, and the add-in
  loads regardless. The zero-warning builds in [status-and-decisions.md](status-and-decisions.md) are
  against the reference-only packages.
- If PowerShell refuses to run the script, check `Get-ExecutionPolicy -List`. An agent's shell often has
  Process scope `Bypass` already. Otherwise the person runs
  `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` (a setting change, so theirs to make).
- The first `dotnet` command on a machine prints the telemetry notice and creates an untrusted ASP.NET
  Core development certificate. Nothing to do.

## 6. Start Revit

Revit reads manifests only at startup and locks the DLL while it runs.

1. **Close a running Revit first.** Ask the person to close it. From an agent's shell,
   `(Get-Process Revit).CloseMainWindow()` returned `False` on the verified machine (the close request
   was not delivered), so do not rely on it, and never kill Revit.
2. **Start it through Explorer**, so that it runs as an ordinary desktop process:

   ```powershell
   Start-Process explorer.exe -ArgumentList "`"$env:ProgramFiles\Autodesk\Revit 2027\Revit.exe`""
   ```

   Explorer hands the launch to the desktop shell, so Revit's parent is the shell's `explorer.exe`, not
   the agent's shell. Started from inside a packaged app, Revit and the Codex processes it runs would
   inherit that app's AppData virtualization, and Family Studio's new folders (`codex-home`, `sessions`)
   would land in the app's private copy. `install.ps1 -Launch` starts Revit from the current shell: fine
   for a person in their own terminal, not for an agent. To confirm the parent:

   ```powershell
   $revit = Get-CimInstance Win32_Process -Filter "Name='Revit.exe'"
   (Get-Process -Id $revit.ParentProcessId).ProcessName   # explorer
   ```

3. **The person answers the security prompt.** About 30 seconds in (under emulation on ARM64), Revit
   shows **Security - Unsigned Add-In** ("The publisher of this add-in could not be verified") with
   **Always Load**, **Load Once** and **Do Not Load** (the default). An agent must not answer it, and
   **Load Once** asks again at every start. The prompt holds up startup, and the journal only records it
   as `TaskDialog_Security_Unsigned_File_Loading`, so watch the titles of Revit's visible windows (for
   example with `EnumWindows`) to know when to tell the person.

## 7. Verify

Search the newest journal, `%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2027\Journals\journal.NNNN.txt`,
for `Family Studio|FamilyStudio`. A good load reads (shortened):

```text
' [Jrn.AddInManifest] Rvt.Attr.AddInManifest: FamilyStudio.addin ... Rvt.Attr.AddInCodeSigningStatus: Unsigned ... Rvt.Attr.AddInLoadFailureMessage: NoError
' 0:< API_SUCCESS { Starting External Application: Family Studio, Class: FamilyStudio.Revit.App, ... Assembly Version: 0.1.0.0 }
' 0:< API_SUCCESS { Added pushbutton ... name: FamilyStudioOpen ... parentId: CustomCtrl_%Family Studio%Design }
' 0:< API_SUCCESS { Registering ThemeChanged event by application Family Studio (016949e7-724a-4c46-973f-0899c23f1739). }
```

Revit's main window title then ends in `[Home]` again, and the ribbon has a **Family Studio** tab. The
person clicks **Family Studio**, then **Sign in with ChatGPT**.

## Reload after a pull

1. Ask the person to close Revit (the loaded DLL is locked).
2. `git pull --ff-only`, and carry any `.env.example` changes into `revit\.env`.
3. Reload PATH (step 1), then `.\scripts\install.ps1 -RevitYear 2027`. The manifest path stays the same.
4. Start Revit through Explorer (step 6). The security prompt may appear again.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `git clone`: `Filename too long` | The target folder is too deep for the 260-character limit | Clone to a short path (step 3) |
| winget: `You cancelled the installation` after about two minutes, exit code 2 | The installer's UAC prompt never reached the person | The person runs it in Terminal (Admin) (step 2) |
| `gh auth login`: `unable to find git executable in PATH` | That terminal started before Git was installed | Reload PATH, then `gh auth setup-git` |
| `git`, `dotnet` or `codex` not found right after an install | The shell's PATH predates the install | Reload PATH (step 1) |
| "Codex was not found" in the Family Studio window | No `codex.exe` on PATH (the winget alias is missing) | `OPENAI_CODEX_PATH` in `revit\.env` (step 4) |
| Files the agent created in a new AppData folder are missing for Revit | Packaged-app AppData virtualization | Keep settings in `revit\.env` (step 4) |
