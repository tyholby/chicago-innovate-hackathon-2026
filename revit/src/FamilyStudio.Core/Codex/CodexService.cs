using System.Diagnostics;
using System.Text.Json;
using FamilyStudio.Core.Json;
using FamilyStudio.Core.Model;
using FamilyStudio.Core.Pipeline;

namespace FamilyStudio.Core.Codex;

/// <param name="CodexHome">Where Codex keeps the ChatGPT sign-in. Family Studio uses its own folder by default.</param>
/// <param name="ExecutablePath">Optional explicit path to the codex executable.</param>
/// <param name="WorkingDirectory">The folder Codex runs in. Nothing is ever written there by Codex (read-only sandbox).</param>
public sealed record CodexOptions(string CodexHome, string? ExecutablePath, string WorkingDirectory, string ClientVersion);

public sealed record CodexAccount(string? Email, string? Plan)
{
    public string Label => string.IsNullOrWhiteSpace(Email) ? "ChatGPT account" : Email!;
}

public sealed record CodexModel(string Id, string DisplayName, string Description, bool IsDefault, string DefaultEffort, IReadOnlyList<string> Efforts)
{
    public override string ToString() => DisplayName;
}

public sealed record SignInOutcome(bool Success, string? Error);

/// <summary>A sign-in in progress. Either open <see cref="BrowserUrl"/>, or show the device code.</summary>
public sealed record SignInAttempt(string LoginId, string? BrowserUrl, string? VerificationUrl, string? UserCode, Task<SignInOutcome> Completion);

/// <summary>
/// Family Studio's connection to ChatGPT through the official Codex app-server. It owns sign-in
/// ("Sign in with ChatGPT", or a device code), the model catalog, and two isolated app-server
/// processes that run stages: one for reasoning and one that may generate images.
/// </summary>
public sealed class CodexService : IStudioAgent
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);

    private readonly CodexOptions _options;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _stageGate = new(1, 1);
    private readonly Dictionary<CodexProfile, CodexProcess> _processes = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _signInGate = new();
    private IReadOnlySet<string>? _features;
    private string[] _disabledMcpServers = Array.Empty<string>();
    private PendingSignIn? _signIn;
    private ActiveStage? _active;
    private bool _disposed;

    public CodexExecutable? Executable { get; private set; }
    public CodexAccount? Account { get; private set; }
    public IReadOnlyList<CodexModel> Models { get; private set; } = Array.Empty<CodexModel>();
    public string CodexHome => _options.CodexHome;

    /// <summary>Where stage events are journaled. Set by the pipeline for each design session.</summary>
    public SessionJournal? Journal { get; set; }

    /// <summary>Raised when the account, sign-in state or model catalog changes. May fire on any thread.</summary>
    public event Action? StateChanged;

    public event Action<string>? Progress;

    public CodexService(CodexOptions options) => _options = options;

    // ---- connection, account and models ---------------------------------------------------

    /// <summary>Finds Codex, starts the reasoning process, and reads the account and model catalog.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await RefreshAccountAsync(cancellationToken).ConfigureAwait(false);
        await RefreshModelsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the model catalog again. The catalog can depend on the account, so this runs after every sign-in.</summary>
    public async Task RefreshModelsAsync(CancellationToken cancellationToken)
    {
        var process = await EnsureProcessAsync(CodexProfile.Reasoning, cancellationToken).ConfigureAwait(false);
        Models = await ReadModelsAsync(process, cancellationToken).ConfigureAwait(false);
        StateChanged?.Invoke();
    }

    public async Task<CodexAccount?> RefreshAccountAsync(CancellationToken cancellationToken)
    {
        var process = await EnsureProcessAsync(CodexProfile.Reasoning, cancellationToken).ConfigureAwait(false);
        var result = await process.Rpc.CallAsync("account/read", new { refreshToken = false }, cancellationToken).ConfigureAwait(false);
        var account = result.Opt("account");
        Account = account?.Str("type") == "chatgpt" ? new CodexAccount(account.Value.Str("email"), account.Value.Str("planType")) : null;
        StateChanged?.Invoke();
        return Account;
    }

    /// <summary>A sign-in Codex is running. Its ID arrives with Codex's answer to account/login/start.</summary>
    private sealed class PendingSignIn
    {
        public string? Id;
        public readonly TaskCompletionSource<SignInOutcome> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Starts "Sign in with ChatGPT". The browser flow returns a URL to open; the device flow returns a code.
    /// Starting a new sign-in cancels the one in progress.
    /// </summary>
    public async Task<SignInAttempt> BeginSignInAsync(bool deviceCode, CancellationToken cancellationToken)
    {
        var process = await EnsureProcessAsync(CodexProfile.Reasoning, cancellationToken).ConfigureAwait(false);
        await CancelSignInAsync().ConfigureAwait(false);
        var pending = new PendingSignIn();
        lock (_signInGate) _signIn = pending;
        try
        {
            var response = await process.Rpc.CallAsync("account/login/start",
                deviceCode ? new { type = "chatgptDeviceCode" } : new { type = "chatgpt" }, cancellationToken).ConfigureAwait(false);
            var id = response.Str("loginId") ?? throw new StudioProtocolException("Codex did not start a sign-in.");
            bool replaced;
            lock (_signInGate) { pending.Id = id; replaced = !ReferenceEquals(_signIn, pending); }
            if (replaced) await CancelLoginAsync(process, id).ConfigureAwait(false); // a newer sign-in started meanwhile
            Journal?.Write("sign_in_started", new { deviceCode });
            return new SignInAttempt(id, response.Str("authUrl"), response.Str("verificationUrl"), response.Str("userCode"), pending.Completion.Task);
        }
        catch (Exception ex)
        {
            lock (_signInGate) if (ReferenceEquals(_signIn, pending)) _signIn = null;
            pending.Completion.TrySetResult(new SignInOutcome(false, ex.Message));
            throw;
        }
    }

    public async Task CancelSignInAsync()
    {
        PendingSignIn? pending;
        lock (_signInGate) { pending = _signIn; _signIn = null; }
        if (pending is null) return;
        pending.Completion.TrySetResult(new SignInOutcome(false, "Sign-in cancelled."));
        if (pending.Id is string id && _processes.TryGetValue(CodexProfile.Reasoning, out var process))
            await CancelLoginAsync(process, id).ConfigureAwait(false);
    }

    private static async Task CancelLoginAsync(CodexProcess process, string loginId)
    {
        if (!process.IsAlive) return;
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.Rpc.CallAsync("account/login/cancel", new { loginId }, grace.Token).ConfigureAwait(false); }
        catch (Exception) { /* already finished or gone */ }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        var process = await EnsureProcessAsync(CodexProfile.Reasoning, cancellationToken).ConfigureAwait(false);
        await process.Rpc.CallAsync("account/logout", new { }, cancellationToken).ConfigureAwait(false);
        Account = null;
        StopProcess(CodexProfile.Image); // it holds the old credentials in memory
        Journal?.Write("signed_out", new { });
        StateChanged?.Invoke();
    }

    private static async Task<IReadOnlyList<CodexModel>> ReadModelsAsync(CodexProcess process, CancellationToken cancellationToken)
    {
        var models = new List<CodexModel>();
        string? cursor = null;
        do
        {
            var page = await process.Rpc.CallAsync("model/list", new { cursor, limit = 100 }, cancellationToken).ConfigureAwait(false);
            foreach (var m in page.Items("data"))
            {
                // Every stage reads images (the reference, and Revit captures in review).
                var images = m.Items("inputModalities").Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "image");
                if (m.True("hidden") || !images || m.Str("id") is not string id) continue;
                var efforts = m.Items("supportedReasoningEfforts").Select(e => e.Str("reasoningEffort")).OfType<string>().ToArray();
                models.Add(new CodexModel(id, m.Str("displayName") ?? id, m.Str("description") ?? "", m.True("isDefault"),
                    m.Str("defaultReasoningEffort") ?? efforts.FirstOrDefault() ?? "medium", efforts));
            }
            cursor = page.Str("nextCursor");
        } while (cursor is not null);
        return models.OrderByDescending(m => m.IsDefault).ToArray();
    }

    // ---- process lifecycle ----------------------------------------------------------------

    private async Task<CodexProcess> EnsureProcessAsync(CodexProfile profile, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_processes.TryGetValue(profile, out var existing) && existing.IsAlive) return existing;
            StopProcess(profile);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            deadline.CancelAfter(StartupTimeout);
            Executable ??= await CodexExecutable.FindAsync(_options.ExecutablePath, deadline.Token).ConfigureAwait(false);
            _features ??= await CodexProcess.KnownFeaturesAsync(Executable, _options.CodexHome, deadline.Token).ConfigureAwait(false);

            for (var attempt = 0; ; attempt++)
            {
                var process = CodexProcess.Start(Executable, _options.CodexHome, _options.WorkingDirectory, profile, _features, _disabledMcpServers);
                try
                {
                    await process.Rpc.CallAsync("initialize", new
                    {
                        clientInfo = new { name = "family_studio", title = "Family Studio", version = _options.ClientVersion },
                        capabilities = new { experimentalApi = true }
                    }, deadline.Token).ConfigureAwait(false);
                    await process.Rpc.NotifyAsync("initialized", new { }, deadline.Token).ConfigureAwait(false);

                    var config = (await process.Rpc.CallAsync("config/read", new { includeLayers = false }, deadline.Token).ConfigureAwait(false)).Opt("config")
                        ?? throw new StudioProtocolException("Codex did not report its effective configuration.");
                    var mcp = CodexProcess.EnabledMcpServers(config);
                    if (mcp.Count > 0)
                    {
                        // Only happens when OPENAI_CODEX_HOME points at a personal Codex setup. Relaunch with them off.
                        if (attempt > 0) throw new StudioProtocolException("Codex kept an MCP server enabled. Remove it from the Codex config or use Family Studio's own Codex home.");
                        _disabledMcpServers = _disabledMcpServers.Union(mcp, StringComparer.Ordinal).ToArray();
                        process.Dispose();
                        continue;
                    }
                    CodexProcess.VerifyConfig(config, profile, _features);
                    if (profile == CodexProfile.Image)
                    {
                        var capabilities = await process.Rpc.CallAsync("modelProvider/capabilities/read", new { }, deadline.Token).ConfigureAwait(false);
                        if (!capabilities.True("imageGeneration"))
                            throw new StudioProtocolException("This Codex version or ChatGPT plan cannot generate images.");
                    }
                    Wire(process);
                    _processes[profile] = process;
                    Journal?.Write("codex_started", new { profile = profile.ToString(), Executable.Version, Executable.Source });
                    return process;
                }
                catch (Exception ex) when (ex is not StudioProtocolException)
                {
                    var diagnostics = process.DiagnosticTail();
                    process.Dispose();
                    if (cancellationToken.IsCancellationRequested) throw;
                    throw new InvalidOperationException(
                        "Codex could not start" + (string.IsNullOrWhiteSpace(diagnostics) ? $": {ex.Message}" : $". Codex reported: {diagnostics}"), ex);
                }
                catch
                {
                    process.Dispose();
                    throw;
                }
            }
        }
        finally { _startGate.Release(); }
    }

    private void Wire(CodexProcess process)
    {
        process.Rpc.Notification += (method, parameters) => OnNotification(process, method, parameters);
        process.Rpc.RequestHandler = (method, parameters, _) => OnServerRequestAsync(process, method, parameters);
        process.Rpc.Closed += error => OnClosed(process, error);
    }

    private void StopProcess(CodexProfile profile)
    {
        if (!_processes.Remove(profile, out var process)) return;
        process.Dispose();
    }

    private void OnClosed(CodexProcess process, Exception error)
    {
        var stage = _active;
        if (stage is not null && ReferenceEquals(stage.Process, process))
            stage.Fail(new IOException("The connection to Codex closed unexpectedly. Try again.", error));
    }

    // ---- stages ---------------------------------------------------------------------------

    private sealed class ActiveStage(StageRequest request, CodexProcess process)
    {
        public readonly object Gate = new();
        public readonly string Id = Guid.NewGuid().ToString("N")[..12];
        public readonly TaskCompletionSource<JsonElement> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public StageRequest Request { get; } = request;
        public CodexProcess Process { get; } = process;
        public string? ThreadId;
        public string? TurnId;
        public string? ActualModel;
        public string FinalText = "";
        public string LastText = "";
        public GeneratedImage? Image;
        public int ImageCount;
        public StageUsage? Usage;
        public Exception? Failure;

        public void Fail(Exception error)
        {
            lock (Gate) Failure ??= error;
            Completion.TrySetException(error);
        }
    }

    public async Task<StageResult> RunAsync(StageRequest request, CancellationToken cancellationToken)
    {
        await _stageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        ActiveStage? stage = null;
        var outcome = "failed";
        try
        {
            if (Account is null && await RefreshAccountAsync(cancellationToken).ConfigureAwait(false) is null)
                throw new StudioSignInRequiredException("Sign in with ChatGPT to use Family Studio.");
            var process = await EnsureProcessAsync(request.Kind == StageKind.Image ? CodexProfile.Image : CodexProfile.Reasoning, cancellationToken).ConfigureAwait(false);
            stage = new ActiveStage(request, process);
            _active = stage;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            deadline.CancelAfter(request.Timeout);
            Journal?.Write("stage_started", new { stageId = stage.Id, request.Name, kind = request.Kind.ToString(), request.Model, images = request.Images.Count });

            try
            {
                var thread = await process.Rpc.CallAsync("thread/start", new
                {
                    model = request.Model.Model,
                    cwd = _options.WorkingDirectory,
                    config = new Dictionary<string, object> { ["model_reasoning_effort"] = request.Model.Effort },
                    ephemeral = true,
                    approvalPolicy = "never",
                    sandbox = "read-only",
                    allowProviderModelFallback = false,
                    environments = Array.Empty<object>(),
                    selectedCapabilityRoots = Array.Empty<object>(),
                    developerInstructions = request.Instructions
                }, deadline.Token).ConfigureAwait(false);
                lock (stage.Gate)
                {
                    stage.ThreadId = thread.Opt("thread")?.Str("id") ?? throw new StudioProtocolException("Codex did not start a thread.");
                    stage.ActualModel = thread.Str("model");
                }

                var input = new List<object> { new { type = "text", text = request.Prompt, text_elements = Array.Empty<object>() } };
                input.AddRange(request.Images.Select(path => (object)new { type = "localImage", path }));
                var turnParameters = new Dictionary<string, object>
                {
                    ["threadId"] = stage.ThreadId,
                    ["input"] = input,
                    ["model"] = request.Model.Model,
                    ["effort"] = request.Model.Effort,
                    ["approvalPolicy"] = "never"
                };
                if (request.OutputSchema is JsonElement schema) turnParameters["outputSchema"] = schema;
                var turn = await process.Rpc.CallAsync("turn/start", turnParameters, deadline.Token).ConfigureAwait(false);
                lock (stage.Gate) stage.TurnId ??= turn.Opt("turn")?.Str("id");

                var completed = await stage.Completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                var status = completed.Str("status");
                if (status == "failed") throw MapError(completed.Opt("error"), "Codex could not finish this step.");
                if (status == "interrupted") throw stage.Failure ?? new OperationCanceledException();
                if (status != "completed") throw new StudioProtocolException($"Codex ended the step with status \"{status}\".");
                if (request.Kind == StageKind.Image && (stage.Image is null || stage.ImageCount != 1))
                    throw new StudioProtocolException("Codex did not return exactly one generated image.");
                var text = stage.FinalText.Length > 0 ? stage.FinalText : stage.LastText;
                if (request.OutputSchema is not null && string.IsNullOrWhiteSpace(text))
                    throw new StudioProtocolException("Codex returned no structured result.");

                outcome = "completed";
                Journal?.Artifact($"{request.Name}-{stage.Id}-output.txt", text);
                return new StageResult(stage.Id, text, stage.Image, stage.Usage, stage.ActualModel, watch.Elapsed);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                outcome = "timed_out";
                await InterruptAsync(stage).ConfigureAwait(false);
                throw new TimeoutException($"The {request.Name} step took longer than {request.Timeout.TotalMinutes:0} minutes and was stopped.");
            }
            catch (OperationCanceledException)
            {
                outcome = "cancelled";
                await InterruptAsync(stage).ConfigureAwait(false);
                throw;
            }
            catch (Exception) when (!stage.Completion.Task.IsCompletedSuccessfully)
            {
                await InterruptAsync(stage).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                // Keep whatever the model said, so a failed step can be diagnosed from the session folder.
                var said = stage.FinalText.Length > 0 ? stage.FinalText : stage.LastText;
                if (said.Length > 0) Journal?.Artifact($"{request.Name}-{stage.Id}-output.txt", said);
                throw;
            }
        }
        finally
        {
            if (stage is not null)
            {
                Journal?.Write("stage_finished", new { stageId = stage.Id, request.Name, outcome, elapsedMs = watch.ElapsedMilliseconds,
                    stage.ActualModel, usage = stage.Usage });
                _active = null;
                if (stage.ThreadId is not null && stage.Process.IsAlive)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await stage.Process.Rpc.CallAsync("thread/unsubscribe", new { threadId = stage.ThreadId }, cleanup.Token).ConfigureAwait(false); }
                    catch (Exception) { /* best effort */ }
                }
            }
            _stageGate.Release();
        }
    }

    private static async Task InterruptAsync(ActiveStage stage)
    {
        string? thread, turn;
        lock (stage.Gate) { thread = stage.ThreadId; turn = stage.TurnId; }
        if (thread is null || turn is null || !stage.Process.IsAlive) return;
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await stage.Process.Rpc.CallAsync("turn/interrupt", new { threadId = thread, turnId = turn }, grace.Token).ConfigureAwait(false);
            await stage.Completion.Task.ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (Exception) { stage.Process.Dispose(); }
    }

    // ---- protocol events ------------------------------------------------------------------

    private void OnNotification(CodexProcess process, string method, JsonElement parameters)
    {
        switch (method)
        {
            case "account/login/completed":
                _ = FinishSignInAsync(parameters.Str("loginId"), parameters.True("success"), parameters.Str("error"));
                return;
            case "account/updated":
                _ = RefreshQuietlyAsync();
                return;
            case "account/rateLimits/updated":
                Journal?.Write("rate_limits", parameters);
                return;
        }

        var stage = _active;
        if (stage is null || !ReferenceEquals(stage.Process, process)) return;
        lock (stage.Gate)
        {
            if (stage.ThreadId is null || parameters.Str("threadId") != stage.ThreadId) return;
            switch (method)
            {
                case "turn/started":
                    stage.TurnId ??= parameters.Opt("turn")?.Str("id");
                    break;
                case "item/started":
                case "item/completed":
                    OnItem(stage, parameters.Opt("item"), method == "item/completed");
                    break;
                case "thread/tokenUsage/updated":
                    if (parameters.Opt("tokenUsage")?.Opt("total") is JsonElement total)
                        stage.Usage = new StageUsage(total.Long("inputTokens"), total.Long("cachedInputTokens"),
                            total.Long("outputTokens"), total.Long("reasoningOutputTokens"), total.Long("totalTokens"));
                    break;
                case "model/rerouted":
                    Journal?.Write("model_rerouted", parameters);
                    Progress?.Invoke($"Codex switched this step to {parameters.Str("toModel")}.");
                    break;
                case "error":
                    if (parameters.True("willRetry")) Progress?.Invoke("The connection to ChatGPT dropped. Retrying...");
                    else stage.Fail(MapError(parameters.Opt("error"), "Codex reported an error."));
                    break;
                case "turn/completed":
                    var turn = parameters.Opt("turn");
                    if (turn is JsonElement t && (stage.TurnId is null || t.Str("id") == stage.TurnId))
                        stage.Completion.TrySetResult(t.Clone());
                    break;
            }
        }
    }

    private void OnItem(ActiveStage stage, JsonElement? itemValue, bool completed)
    {
        if (itemValue is not JsonElement item) return;
        var type = item.Str("type") ?? "";
        Journal?.Write("item", new { stageId = stage.Id, type, status = item.Str("status"), completed });
        switch (type)
        {
            case "userMessage" or "reasoning" or "contextCompaction" or "plan" or "hookPrompt":
                if (!completed && type == "reasoning") Progress?.Invoke("Thinking...");
                return;
            case "agentMessage":
                if (!completed) { Progress?.Invoke("Writing the result..."); return; }
                var text = item.Str("text") ?? "";
                stage.LastText = text;
                if (item.Str("phase") is null or "final_answer") stage.FinalText = text;
                return;
            case "imageGeneration" when stage.Request.Kind == StageKind.Image:
                if (!completed) { Progress?.Invoke("Drawing the reference image..."); return; }
                if (item.Opt("failure") is JsonElement failure)
                {
                    stage.Fail(failure.Str("type") == "usageLimitExceeded"
                        ? new StudioLimitException("Your ChatGPT plan's image generation limit was reached.", failure.ResetsAt())
                        : new StudioProtocolException("Image generation failed."));
                    return;
                }
                if (item.Str("status") != "completed" || item.Str("savedPath") is not string saved || !File.Exists(saved))
                {
                    stage.Fail(new StudioProtocolException("Codex did not save the generated image."));
                    return;
                }
                stage.ImageCount++;
                stage.Image = new GeneratedImage(item.Str("id") ?? "", saved, item.Str("revisedPrompt"));
                return;
            case "commandExecution" or "fileChange" or "mcpToolCall" or "dynamicToolCall" or "webSearch" or "imageView"
                or "collabAgentToolCall" or "subAgentActivity" or "imageGeneration":
                Journal?.Write("tool_blocked", new { stageId = stage.Id, type });
                stage.Fail(new StudioProtocolException($"Codex tried to use a tool ({type}) that Family Studio does not allow. The step was stopped."));
                return;
            default:
                Journal?.Write("unknown_item", new { stageId = stage.Id, type });
                return;
        }
    }

    private Task<object?> OnServerRequestAsync(CodexProcess process, string method, JsonElement parameters)
    {
        switch (method)
        {
            case "currentTime/read":
                return Task.FromResult<object?>(new { currentTimeAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            case "mcpServer/elicitation/request":
                return Task.FromResult<object?>(new { action = "decline" });
        }
        var stage = _active;
        if (stage is not null && ReferenceEquals(stage.Process, process))
        {
            Journal?.Write("request_blocked", new { stageId = stage.Id, method });
            stage.Fail(new StudioProtocolException($"Codex asked for something Family Studio does not allow ({method}). The step was stopped."));
        }
        return Task.FromException<object?>(new NotSupportedException($"Family Studio does not allow {method}."));
    }

    private async Task FinishSignInAsync(string? loginId, bool success, string? error)
    {
        if (success)
        {
            // Any finished sign-in signs the user in, even one that was replaced by a newer attempt.
            StopProcess(CodexProfile.Image); // it holds the old credentials in memory
            await RefreshQuietlyAsync().ConfigureAwait(false);
            try { await RefreshModelsAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (Exception) { /* the catalog from before the sign-in stays */ }
        }
        PendingSignIn? pending;
        lock (_signInGate)
        {
            // A failure only ends the attempt it belongs to. A cancelled attempt reports its failure
            // too, possibly before the newer attempt has its ID, so an unmatched failure is ignored.
            pending = _signIn;
            if (pending is not null && (success || loginId is null || pending.Id == loginId)) _signIn = null;
            else pending = null;
        }
        Journal?.Write("sign_in_finished", new { success, current = pending is not null });
        pending?.Completion.TrySetResult(success && Account is not null
            ? new SignInOutcome(true, null)
            : new SignInOutcome(false, success ? "Codex finished the sign-in but reports no ChatGPT account. Try again." : error ?? "Sign-in did not complete."));
    }

    private async Task RefreshQuietlyAsync()
    {
        try { await RefreshAccountAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (Exception) { /* the next explicit action reports the problem */ }
    }

    private Exception MapError(JsonElement? error, string fallback)
    {
        var message = error?.Str("message") ?? fallback;
        var info = error?.Opt("codexErrorInfo");
        var raw = info?.GetRawText() ?? "";
        if (raw.Contains("usageLimitExceeded", StringComparison.Ordinal) || raw.Contains("rateLimitExceeded", StringComparison.Ordinal))
            return new StudioLimitException(message, info is JsonElement i && i.ValueKind == JsonValueKind.Object ? i.ResetsAt() : null);
        if (raw.Contains("unauthorized", StringComparison.Ordinal))
        {
            Account = null;
            StateChanged?.Invoke();
            return new StudioSignInRequiredException("Your ChatGPT sign-in expired. Sign in again to continue.");
        }
        return new IOException(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        PendingSignIn? pending;
        lock (_signInGate) { pending = _signIn; _signIn = null; }
        pending?.Completion.TrySetResult(new SignInOutcome(false, "Family Studio closed."));
        foreach (var profile in _processes.Keys.ToArray()) StopProcess(profile);
    }
}
