using System.Text.RegularExpressions;

namespace FamilyStudio.Core.Config;

/// <summary>
/// A minimal .env reader: KEY=VALUE lines, # comments, optional "export ", optional surrounding
/// quotes. Blank values are skipped so an unfilled template line never overrides anything.
/// </summary>
public static partial class EnvFile
{
    public static Dictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var match = Assignment().Match(line);
            if (!match.Success) continue;
            var value = match.Groups["value"].Value.Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];
            else
            {
                var comment = value.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0) value = value[..comment].TrimEnd();
            }
            if (value.Length > 0) values[match.Groups["key"].Value] = value;
        }
        return values;
    }

    public static Dictionary<string, string> Read(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(?:export\s+)?(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>.*)$")]
    private static partial Regex Assignment();
}
