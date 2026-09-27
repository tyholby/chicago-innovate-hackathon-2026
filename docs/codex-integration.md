# Codex integration

Family Studio's AI runs on the user's ChatGPT plan through the official **Codex app-server**
(`codex app-server`), spoken to over JSON-RPC on stdio. Codex owns the ChatGPT sign-in and refreshes it;
Family Studio never sees a password or a token. Code: `revit/src/FamilyStudio.Core/Codex/`.

Why this route: the app-server is the interface OpenAI's own editor integrations use. Its protocol also
has a `chatgptAuthTokens` login that accepts raw tokens, but the protocol marks it
"[UNSTABLE] FOR OPENAI INTERNAL USE ONLY", so a pasteable ChatGPT credential is deliberately not
supported.

## Finding Codex

`CodexExecutable.FindAsync` tries, in order, and probes each candidate with `--version` (5 s timeout):

1. `OPENAI_CODEX_PATH` from the environment or `.env`.
2. `PATH` (`codex.exe` on Windows, `codex` elsewhere). npm's `codex.cmd` shim cannot be launched
   directly, so it is not used.
3. Install locations, newest version wins:
   - Windows: `%LOCALAPPDATA%\OpenAI\Codex\bin\**\codex.exe` (Codex app),
     `%APPDATA%\npm\node_modules\@openai\codex\vendor\**\codex.exe` (npm package),
     `%LOCALAPPDATA%\Programs\ChatGPT\**\codex.exe`.
   - macOS: `/Applications/Codex.app/.../codex`, `/Applications/ChatGPT.app/Contents/Resources/codex`,
     Homebrew and `/usr/local/bin`.

## Codex home and working folder

- `CODEX_HOME` is where Codex stores the sign-in (`auth.json`) and its own state. Family Studio sets it
  for its child processes to `%LOCALAPPDATA%\FamilyStudio\codex-home` by default, a folder no personal
  Codex setup touches. `OPENAI_CODEX_HOME` overrides it, for example with `%USERPROFILE%\.codex` to reuse
  the Codex app's sign-in. The Rhino panel reads the same default to report sign-in status.
- The processes run in `%LOCALAPPDATA%\FamilyStudio\codex-workspace`, an empty folder, so no project
  `AGENTS.md`, `.codex` config or skills are picked up.

## Two isolated processes

`CodexService` starts at most two app-servers, lazily:

| Profile | Used by | Image generation | Code mode |
| --- | --- | --- | --- |
| `Reasoning` | Brief, recipe, layout, review, repair, refine | off | off |
| `Image` | The reference image, and View2Render renders | on | on |

Launch (`CodexProcess.Arguments`):

```
codex --disable <feature>... --enable|--disable image_generation -c <key>=<value>... [-c mcp_servers={...}] app-server
```

- **Feature flags.** Only names the installed Codex reports in `codex features list` (excluding stage
  "removed") are passed. **An unknown `--disable` name makes app-server exit with
  "Unknown feature flag".** Disabled in both processes: `shell_tool`, `unified_exec`, `view_image`,
  `apps`, `plugins`, `remote_plugin`, `hooks`, `memories`, `multi_agent`, `multi_agent_v2`, `browser_use`,
  `browser_use_external`, `computer_use`, `in_app_browser`, `goals`, `skill_search`,
  `skill_mcp_dependency_install`, `tool_suggest`, `sleep_tool`, `tool_call_mcp_elicitation`,
  `workspace_dependencies`.
- **Code mode.** Codex runs hosted tool calls, image generation included, through its code-mode host.
  With `code_mode` / `code_mode_host` off, the image tool still appears in the model's tool list but every
  call fails and the model answers "the image-generation tool is unavailable". They are therefore off only
  in the reasoning process.
- **Config overrides** (every one checked through `config/read` after start): `approval_policy="never"`,
  `sandbox_mode="read-only"`, `web_search="disabled"`, `forced_login_method="chatgpt"`, `notify=[]`,
  `check_for_update_on_startup=false`, `include_environment_context=false`,
  `include_permissions_instructions=false`, `include_apps_instructions=false`,
  `history.persistence="none"`, `project_doc_max_bytes=0`.
- **MCP servers.** If `config/read` shows MCP servers enabled (only possible when `OPENAI_CODEX_HOME`
  points at a personal Codex setup), the process is relaunched once with
  `mcp_servers={"<name>"={enabled=false},...}`. The names are quoted TOML keys because dotted `-c` paths
  split names that contain dots.

Handshake per process: `initialize` (client `family_studio`, `capabilities.experimentalApi = true`),
the `initialized` notification, `config/read` and verification, and for the image process
`modelProvider/capabilities/read`, which must report `imageGeneration`. Startup has a 45 s limit; if the
process dies, the last lines of its stderr are included in the error.

## Account and models

- `account/read {refreshToken:false}`: signed in only when `account.type == "chatgpt"`.
- `model/list` (paged): Family Studio offers models that are not hidden and accept image input, default
  first. Effort levels come from each model's `supportedReasoningEfforts`. The view model picks
  `OPENAI_CODEX_MODEL` / `OPENAI_CODEX_REASONING_EFFORT` when set, otherwise the catalog default at "low"
  (or the model's own default, for models without it: `CodexModel.StartingEffort`). Higher efforts are one
  click away in the settings menu.
- Sign in: `account/login/start` with `{type:"chatgpt"}` returns `authUrl` (opened in the browser;
  Codex listens on localhost for the callback) or with `{type:"chatgptDeviceCode"}` returns
  `verificationUrl` and `userCode`. Completion arrives as the `account/login/completed` notification.
  After a success the image process is restarted so it loads the new credentials, and the model
  catalog is read again.
- One sign-in at a time. Starting another (**Use a code instead**) cancels the first with
  `account/login/cancel {loginId}`. Codex then reports the cancelled attempt as a failed
  `account/login/completed` (verified), so completions are matched by `loginId`: an unmatched failure is
  ignored, and any success signs the user in. `account/logout` signs out.

## Running a stage

`CodexService.RunAsync(StageRequest)`, up to four stages at once (`MaxConcurrentStages`; a collection
plans its recipes in parallel), each in its own thread. Notifications are routed to their stage by
`threadId`:

1. `thread/start` with `model`, `cwd`, `config.model_reasoning_effort`, `ephemeral:true`,
   `approvalPolicy:"never"`, `sandbox:"read-only"`, `allowProviderModelFallback:false`,
   `environments:[]`, `selectedCapabilityRoots:[]` and the stage's `developerInstructions`.
2. `turn/start` with `input` (the prompt as `text`, images as `localImage` paths), `model`, `effort`,
   `approvalPolicy:"never"` and, for structured stages, `outputSchema` (strict JSON Schema).
3. Notifications for the thread:
   - `item/started` / `item/completed`: `agentMessage` gives the answer (the last one whose `phase` is
     `final_answer` or absent); `imageGeneration` (image stage only) must complete with a `savedPath`
     (Codex saves under `CODEX_HOME/generated_images/`); `reasoning`, `userMessage`, `plan`,
     `contextCompaction` and `hookPrompt` are ignored.
   - Any tool activity (`commandExecution`, `fileChange`, `mcpToolCall`, `dynamicToolCall`, `webSearch`,
     `imageView`, `collabAgentToolCall`, `subAgentActivity`, or `imageGeneration` in a reasoning stage)
     fails the stage and interrupts the turn. Unknown item types are logged and ignored.
   - `item/agentMessage/delta` (the answer as it streams: measured for progress) and
     `item/reasoning/summaryTextDelta` / `textDelta` (the model is thinking).
   - `thread/tokenUsage/updated` (usage totals), `model/rerouted` (logged), `error` (retried by Codex
     when `willRetry`, otherwise fails the stage), `turn/completed` (status and error).
4. Server-to-client requests: `currentTime/read` is answered, MCP elicitations are declined, and anything
   else (tool calls, approvals, user-input requests) is refused and fails the stage.
5. `thread/unsubscribe` when done.

**Progress.** Every running stage raises `StageProgress` once a second and at each phase change
(starting, thinking, writing, drawing, finished): the time so far, the characters streamed, and how often
the request's `CountKey` has appeared (`"shape"`, once per recipe part; `"key"`, once per placement). The
session shows it on the detail line ("Writing, 23 parts so far (4:10)"; one entry per item while a
collection plans in parallel). Reports are never journaled, and an exception in a listener is swallowed,
because they run on timer and protocol threads.

**The image model.** Codex's hosted image tool chooses it, and the protocol has no setting for it: the
`imageGeneration` item carries `status`, `savedPath`, `revisedPrompt` and `failure`, not a model name
(checked against the Codex 0.157.1 schema). OpenAI says ChatGPT Images 2.5 serves Codex users, but Family
Studio can neither pin that nor show which model drew an image. Input images go in as `localImage`
items: View2Render sends the view capture first, then the reference images.

A deadline or a cancel sends `turn/interrupt` (10 s grace). Errors map to: `usageLimitExceeded` or
`rateLimitExceeded` to `StudioLimitException` (with the reset time when reported), `unauthorized` to
`StudioSignInRequiredException` (the account is cleared), anything else to `IOException` with Codex's
message.

## Debugging

- Every stage writes `stage_started`, `item` (type and status only), `stage_finished` (outcome, time,
  model, token usage), and on problems `tool_blocked`, `request_blocked` or `unknown_item` to the
  session's `events.jsonl`. The model's answer is saved as `<stage>-<id>-output.txt`, also on failure.
- Run the pipeline without Revit:
  `dotnet run --project revit/tools/FamilyStudio.Probe -- single "a walnut stool" --effort low`.
  Set `OPENAI_CODEX_HOME` to use an existing sign-in; `FAMILY_STUDIO_OUTPUT_DIR` to choose the folder.
- Check a new Codex version before relying on it:
  - `codex features list` for flags that were renamed or removed;
  - `codex app-server generate-json-schema --out <dir> --experimental` for the exact protocol types.

`codex app-server` exits as soon as its standard input closes (verified), so it cannot outlive Revit,
even after a crash. Family Studio also kills both processes when its window closes.

Verified on 2026-09-26 with Codex 0.154.0-alpha.6.2 (macOS, bundled with the ChatGPT app): brief about
15 s, reference image about 40 to 45 s, one recipe about 50 to 60 s at medium effort.
