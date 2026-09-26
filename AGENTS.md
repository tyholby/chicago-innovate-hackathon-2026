# Agent guide

Instructions for coding agents working in this repository. Read this first, then the doc for the
area you are changing.

## What this is

**Family Studio** turns a description or a product photo into a native Revit furniture family. It
runs on the user's ChatGPT plan through OpenAI's Codex app-server ("Sign in with ChatGPT"); there are
no API keys. `revit/` is the working product (single items and seven-item collections). `rhino/` is a
Rhino 8 scaffold that shows a hello-world panel in the same design language.

## Map

| Path | What |
| --- | --- |
| `revit/src/FamilyStudio.Core/` | Plain net8.0, no Revit, no package references. Model, validation, prompts, output schemas, Codex client, pipeline |
| `revit/src/FamilyStudio.Revit/` | The add-in: ribbon (`App.cs`), launcher, native Revit host (`Native/`), WPF window and design system (`UI/`) |
| `revit/tests/FamilyStudio.Core.Tests/` | xUnit tests for the core. Run on any OS |
| `revit/tools/FamilyStudio.Probe/` | The real pipeline on the command line, with an in-memory stand-in for Revit |
| `revit/scripts/` | `build.ps1`, `install.ps1`, `uninstall.ps1` (Windows) |
| `rhino/src/FamilyStudio.Rhino/` | Rhino 8 plug-in (`.rhp`): plug-in class, `FamilyStudio` command, Eto panel |
| `rhino/scripts/` | `build.ps1`, `run.ps1` (dev load via `RHINO_PACKAGE_DIRS`), `install.ps1` (local Yak package) |
| `docs/` | Design and engineering docs (below) |

## Docs

| Doc | Read before |
| --- | --- |
| [docs/architecture.md](docs/architecture.md) | Any change: components, lifecycle, threading, state machine, session folder |
| [docs/pipeline.md](docs/pipeline.md) | Prompts, output schemas, geometry rules, tolerances, placement, corrections, review |
| [docs/codex-integration.md](docs/codex-integration.md) | Anything touching Codex: launch, isolation, sign-in, stages, debugging |
| [docs/revit-integration.md](docs/revit-integration.md) | The add-in, Revit API work, builds per Revit year, install |
| [docs/local-install.md](docs/local-install.md) | Installing or reloading the Revit add-in on someone's Windows machine: prerequisites, admin steps, the security prompt |
| [docs/rhino-integration.md](docs/rhino-integration.md) | The Rhino plug-in: targets, IDs, loading, bringing the pipeline over |
| [docs/design-system.md](docs/design-system.md) | Any UI work, in Revit or Rhino |
| [docs/status-and-decisions.md](docs/status-and-decisions.md) | What is verified, what is not, and decisions already made |

## Commands

Install the .NET SDK 8 and 10 (10 is needed for Revit 2027 builds).

```bash
dotnet test revit/tests/FamilyStudio.Core.Tests                               # core tests, any OS
dotnet build revit/src/FamilyStudio.Revit -c Release -p:RevitYear=2026        # also 2025 and 2027
dotnet build rhino/src/FamilyStudio.Rhino -c Release                          # net7.0 and net48
dotnet run --project revit/tools/FamilyStudio.Probe -- status                 # Codex and account check
```

Both plug-ins compile on macOS and Linux: the Revit project uses `EnableWindowsTargeting` and falls back
to reference-only Revit API packages when no local Revit is installed, and the Rhino project pulls .NET
Framework reference assemblies for its net48 lane. They only **run** on Windows inside Revit or Rhino.

On Windows, `revit\scripts\install.ps1 -RevitYear 2026 -Launch` builds, registers and starts Revit, and
`rhino\scripts\run.ps1` builds and starts Rhino 8 with the plug-in loaded from the build folder. Setting
up or reloading the Revit add-in on someone's machine as an agent: follow
[docs/local-install.md](docs/local-install.md), which starts Revit through Explorer instead of `-Launch`.

## Before you finish a change

1. `dotnet test revit/tests/FamilyStudio.Core.Tests` passes.
2. The add-in builds with zero warnings for `RevitYear` 2025, 2026 and 2027, and Rhino builds for both
   target frameworks.
3. If you changed prompts, schemas or the Codex client, a live probe run still works
   (`single "..."`). **Live runs spend the user's ChatGPT usage: ask before running them.**
4. Say plainly what you could not verify. Nothing in `FamilyStudio.Revit` or the Rhino panel can be run
   outside Windows, so UI and Revit API changes need a note for the person testing on Windows.

## Rules

- **This repository is public.** Never commit `.env`, API keys, Codex `auth.json`, customer data or
  personal paths. Only `.env.example` files are tracked.
- **No em dashes** in anything a user or reader sees: UI strings, error messages, docs, READMEs, prompts.
  Use a colon, period, comma or parentheses. Write "in development", never "coming soon".
- **C# 12.** `LangVersion` is pinned in both `Directory.Build.props` files so SDK 8 and SDK 10 build the
  same code. Do not use C# 13 or 14 features.
- **The core has no package references** and uses only what .NET 8 ships (System.Text.Json 8 APIs).
  Revit 2025 and 2026 load add-ins into Revit's own context, where a newer System.Text.Json than the one
  Revit already loaded cannot bind. Do not add NuGet packages to `FamilyStudio.Core` or
  `FamilyStudio.Revit`.
- **Revit API calls only through `RevitDispatcher`.** Every Revit call runs inside a queued
  `ExternalEvent` work item. Never hold a transaction open while waiting on AI, and never touch Revit
  objects from Codex or thread-pool threads.
- **UI state on the UI thread.** The view model refreshes from session and account events in its own
  dispatcher operation (`OnBackgroundChange`). Mutate bound `ObservableCollection`s only on the UI
  thread, and after state changes that happen without user input call `RaiseEverything` so commands
  re-query (WPF only does that after input).
- **WPF projects do not get `System.IO` as an implicit using** (it clashes with
  `System.Windows.Shapes.Path`). Add `using System.IO;` where needed.
- **Theme colours are `DynamicResource` tokens** from `UI/Theme/Paper.xaml` and `Night.xaml`. Never
  hard-code a colour in a view; follow [docs/design-system.md](docs/design-system.md).
- **Prompts and schemas move together.** When a record that the model returns changes, change its schema
  in `Json/OutputSchemas.cs` (the schema tests compare them) and bump `StudioPrompts.Version`.
- **Codex feature flags must exist in the installed Codex.** An unknown `--disable` flag makes
  `codex app-server` exit at startup. The client reads `codex features list` and only passes known names.
