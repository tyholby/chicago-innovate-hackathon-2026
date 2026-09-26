# Chicago Innovate Hackathon 2026

**Family Studio**: words or a photo in, a native Revit family out.

An architect describes a piece of furniture, or drops in a product photo, and Family Studio drafts a
reference, checks the dimensions and finishes with them, and builds a real Revit Furniture family:
native solids, named materials, one type, ready to load into any project. It runs on the architect's
own ChatGPT plan, signing in with ChatGPT through OpenAI's Codex app.

This is a monorepo with one folder per host application:

| Folder | What | Status |
| --- | --- | --- |
| [`revit/`](revit/) | Family Studio for Revit 2025, 2026 and 2027 | Working: single items and seven-item collections |
| [`rhino/`](rhino/) | Family Studio for Rhino 8 | Scaffold: a hello-world panel in the same design language |
| [`docs/`](docs/) | [The design system](docs/design-system.md) shared by both, and engineering docs | Coding agents start at [AGENTS.md](AGENTS.md) |

## Quick start (Windows)

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) (8, or 10 for Revit 2027) and
   [Codex](https://developers.openai.com/codex) (the desktop app, or `npm install -g @openai/codex`).
2. Revit: in `revit/`, run `.\scripts\install.ps1`, open Revit, click **Family Studio** on the
   ribbon and **Sign in with ChatGPT**. See [revit/README.md](revit/README.md).
3. Rhino: in `rhino/`, run `.\scripts\run.ps1`, then the command `FamilyStudio`.
   See [rhino/README.md](rhino/README.md).

## Settings and credentials

Each folder has a `.env.example`. Copy it to `.env` in the same folder; `.env` files are ignored by
git and never committed.

- **OpenAI (ChatGPT through Codex).** Nothing to paste: sign in from the plugin window and Codex keeps
  the sign-in refreshed in `%LOCALAPPDATA%\FamilyStudio\codex-home`. Set `OPENAI_CODEX_HOME` to reuse
  another Codex sign-in, such as `%USERPROFILE%\.codex`.
- **OpenRouter.** `OPENROUTER_API_KEY` is read by both plugins and reserved for features in development.

## License

[MIT](LICENSE)
