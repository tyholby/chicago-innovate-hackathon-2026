namespace FamilyStudio.Core.Config;

/// <summary>
/// Settings read from the environment and .env files. Process environment variables win, then a
/// .env file beside the plugin, then %APPDATA%\FamilyStudio\.env. Nothing here is ever written
/// to logs; the OpenRouter key in particular is only exposed as "configured" or "not configured".
/// </summary>
public sealed class StudioEnvironment
{
    public const string CodexHomeKey = "OPENAI_CODEX_HOME";
    public const string CodexPathKey = "OPENAI_CODEX_PATH";
    public const string ModelKey = "OPENAI_CODEX_MODEL";
    public const string EffortKey = "OPENAI_CODEX_REASONING_EFFORT";
    public const string OpenRouterKeyKey = "OPENROUTER_API_KEY";
    public const string OutputKey = "FAMILY_STUDIO_OUTPUT_DIR";
    public const string TemplateKey = "FAMILY_STUDIO_FURNITURE_TEMPLATE";

    private readonly Dictionary<string, string> _values;

    /// <summary>The .env files that were found and read, in precedence order.</summary>
    public IReadOnlyList<string> LoadedFiles { get; }

    private StudioEnvironment(Dictionary<string, string> values, IReadOnlyList<string> loadedFiles)
    {
        _values = values;
        LoadedFiles = loadedFiles;
    }

    /// <param name="pluginDirectory">The folder the plugin assembly was loaded from.</param>
    public static StudioEnvironment Load(string pluginDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(pluginDirectory, ".env"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FamilyStudio", ".env")
        };
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var loaded = new List<string>();
        // Lowest precedence first, so later sources overwrite.
        foreach (var file in Enumerable.Reverse(candidates).Where(File.Exists))
        {
            foreach (var (key, value) in EnvFile.Read(file)) values[key] = value;
            loaded.Insert(0, file);
        }
        foreach (var key in new[] { CodexHomeKey, CodexPathKey, ModelKey, EffortKey, OpenRouterKeyKey, OutputKey, TemplateKey })
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } fromProcess) values[key] = fromProcess;
        return new StudioEnvironment(values, loaded);
    }

    public static StudioEnvironment FromValues(IDictionary<string, string> values) =>
        new(new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase), Array.Empty<string>());

    private string? Get(string key) => _values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string ExpandPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded.StartsWith('~'))
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + expanded[1..];
        return Path.GetFullPath(expanded);
    }

    private static string AppData(params string[] parts) =>
        Path.Combine(new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FamilyStudio" }.Concat(parts).ToArray());

    /// <summary>
    /// Where Codex stores the ChatGPT sign-in. Defaults to Family Studio's own folder, isolated from
    /// any personal Codex setup. Point it at %USERPROFILE%\.codex to reuse the Codex app's sign-in.
    /// </summary>
    public string CodexHome => Get(CodexHomeKey) is string home ? ExpandPath(home) : AppData("codex-home");

    public bool UsesOwnCodexHome => Get(CodexHomeKey) is null;

    public string? CodexPath => Get(CodexPathKey) is string path ? ExpandPath(path) : null;

    public string? PreferredModel => Get(ModelKey);

    public string? PreferredEffort => Get(EffortKey)?.ToLowerInvariant();

    public string OutputRoot => Get(OutputKey) is string output ? ExpandPath(output) : AppData("sessions");

    /// <summary>An explicit furniture family template (.rft), for Revit installs without the default one.</summary>
    public string? FurnitureTemplate => Get(TemplateKey) is string template ? ExpandPath(template) : null;

    /// <summary>Codex runs here: an empty folder, so no project instructions or skills are picked up.</summary>
    public string CodexWorkingDirectory => AppData("codex-workspace");

    public bool HasOpenRouterKey => Get(OpenRouterKeyKey) is not null;

    /// <summary>Reserved for OpenRouter-backed features. Never log or display this value.</summary>
    public string? OpenRouterApiKey => Get(OpenRouterKeyKey);
}
