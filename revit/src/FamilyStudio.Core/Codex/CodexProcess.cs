using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FamilyStudio.Core.Json;

namespace FamilyStudio.Core.Codex;

/// <summary>
/// Which of the two app-server processes a stage runs in. Image generation is switched on only in
/// the Image process, so a reasoning stage cannot generate images even if a model tried to.
/// </summary>
public enum CodexProfile { Reasoning, Image }

/// <summary>
/// One hidden <c>codex app-server</c> process, launched with everything this plugin does not need
/// switched off: no shell, no file edits, no browser, no web search, no plugins, apps, hooks,
/// memories or MCP servers, no approvals and a read-only sandbox.
/// </summary>
public sealed partial class CodexProcess : IDisposable
{
    /// <summary>Features disabled in both processes when this Codex version knows them. Unknown names are skipped, never passed.</summary>
    internal static readonly string[] DisabledFeatures =
    {
        "shell_tool", "unified_exec", "view_image", "apps", "plugins", "remote_plugin", "hooks", "memories",
        "multi_agent", "multi_agent_v2", "browser_use", "browser_use_external", "computer_use", "in_app_browser",
        "goals", "skill_search", "skill_mcp_dependency_install", "tool_suggest", "sleep_tool",
        "tool_call_mcp_elicitation", "workspace_dependencies"
    };

    /// <summary>
    /// Codex routes hosted tool calls, image generation included, through its code-mode host. Reasoning
    /// stages call no tools at all, so it is switched off there and left on only for the image process.
    /// </summary>
    internal static readonly string[] ToolHostFeatures = { "code_mode", "code_mode_host" };

    internal static IEnumerable<string> DisabledFor(CodexProfile profile) =>
        profile == CodexProfile.Image ? DisabledFeatures : DisabledFeatures.Concat(ToolHostFeatures);

    /// <summary>Config overrides applied to every process. Each is verified through config/read after start.</summary>
    internal static readonly (string Key, string Value)[] ConfigOverrides =
    {
        ("approval_policy", "\"never\""),
        ("sandbox_mode", "\"read-only\""),
        ("web_search", "\"disabled\""),
        ("forced_login_method", "\"chatgpt\""),
        ("notify", "[]"),
        ("check_for_update_on_startup", "false"),
        ("include_environment_context", "false"),
        ("include_permissions_instructions", "false"),
        ("include_apps_instructions", "false"),
        ("history.persistence", "\"none\""),
        ("project_doc_max_bytes", "0")
    };

    private readonly Process _process;
    private readonly LinkedList<string> _stderrTail = new();
    private readonly Task _stderrPump;

    public CodexProfile Profile { get; }
    public JsonRpcConnection Rpc { get; }
    public bool IsAlive
    {
        get
        {
            try { return Rpc.IsOpen && !_process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    private CodexProcess(Process process, CodexProfile profile)
    {
        _process = process;
        Profile = profile;
        Rpc = new JsonRpcConnection(process.StandardOutput, process.StandardInput);
        _stderrPump = Task.Run(PumpStderrAsync);
    }

    public static IReadOnlyList<string> Arguments(CodexProfile profile, IReadOnlySet<string> knownFeatures, IReadOnlyCollection<string> mcpServersToDisable)
    {
        var args = new List<string>();
        foreach (var feature in DisabledFor(profile).Where(knownFeatures.Contains))
            args.AddRange(new[] { "--disable", feature });
        if (knownFeatures.Contains("image_generation"))
            args.AddRange(new[] { profile == CodexProfile.Image ? "--enable" : "--disable", "image_generation" });
        foreach (var (key, value) in ConfigOverrides)
            args.AddRange(new[] { "-c", $"{key}={value}" });
        if (mcpServersToDisable.Count > 0)
        {
            // A TOML inline table keyed by quoted server names. Dotted -c paths would split names that contain dots.
            var entries = string.Join(",", mcpServersToDisable.Select(n => $"{JsonSerializer.Serialize(n)}={{enabled=false}}"));
            args.AddRange(new[] { "-c", $"mcp_servers={{{entries}}}" });
        }
        args.Add("app-server");
        return args;
    }

    public static CodexProcess Start(CodexExecutable executable, string codexHome, string workingDirectory, CodexProfile profile,
        IReadOnlySet<string> knownFeatures, IReadOnlyCollection<string> mcpServersToDisable)
    {
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(workingDirectory);
        var info = new ProcessStartInfo(executable.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in Arguments(profile, knownFeatures, mcpServersToDisable)) info.ArgumentList.Add(argument);
        info.Environment["CODEX_HOME"] = codexHome;
        var process = Process.Start(info) ?? throw new InvalidOperationException("Codex did not start.");
        return new CodexProcess(process, profile);
    }

    /// <summary>Reads the feature names this Codex version accepts (stage "removed" excluded).</summary>
    public static async Task<IReadOnlySet<string>> KnownFeaturesAsync(CodexExecutable executable, string codexHome, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(codexHome);
        var output = await CodexExecutable.RunAsync(executable.Path, new[] { "features", "list" },
            new Dictionary<string, string> { ["CODEX_HOME"] = codexHome }, cancellationToken).ConfigureAwait(false);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in (output ?? "").Split('\n'))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 2 && FeatureName().IsMatch(columns[0]) && !line.Contains(" removed ", StringComparison.Ordinal))
                names.Add(columns[0]);
        }
        return names;
    }

    /// <summary>Checks the effective configuration really is what this plugin asked for.</summary>
    public static void VerifyConfig(JsonElement config, CodexProfile profile, IReadOnlySet<string> knownFeatures)
    {
        if (config.Str("approval_policy") != "never" || config.Str("sandbox_mode") != "read-only" || config.Str("web_search") != "disabled")
            throw new InvalidOperationException("Codex did not apply Family Studio's read-only, no-approval settings.");
        var features = config.Opt("features");
        if (features is JsonElement f)
        {
            foreach (var name in DisabledFor(profile).Where(knownFeatures.Contains))
                if (f.Opt(name)?.ValueKind == JsonValueKind.True)
                    throw new InvalidOperationException($"Codex did not switch off \"{name}\".");
            if (knownFeatures.Contains("image_generation") && f.True("image_generation") != (profile == CodexProfile.Image))
                throw new InvalidOperationException("Codex did not apply the image-generation setting for this process.");
        }
    }

    /// <summary>MCP servers that are still enabled in the effective configuration.</summary>
    public static IReadOnlyList<string> EnabledMcpServers(JsonElement config) =>
        config.Opt("mcp_servers") is { ValueKind: JsonValueKind.Object } servers
            ? servers.EnumerateObject().Where(s => s.Value.Opt("enabled")?.ValueKind != JsonValueKind.False).Select(s => s.Name).ToArray()
            : Array.Empty<string>();

    /// <summary>The last few lines Codex wrote to stderr, without terminal colour codes. For error messages only.</summary>
    public string DiagnosticTail()
    {
        lock (_stderrTail) return string.Join(Environment.NewLine, _stderrTail);
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var clean = AnsiEscape().Replace(line, "").Trim();
                if (clean.Length == 0) continue;
                lock (_stderrTail)
                {
                    _stderrTail.AddLast(clean.Length > 400 ? clean[..400] : clean);
                    while (_stderrTail.Count > 8) _stderrTail.RemoveFirst();
                }
            }
        }
        catch (Exception) { /* the process is gone */ }
    }

    public void Dispose()
    {
        Rpc.Dispose();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        _ = _stderrPump.ContinueWith(_ => _process.Dispose(), TaskScheduler.Default);
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_.]*$")]
    private static partial Regex FeatureName();

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscape();
}
