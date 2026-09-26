using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace FamilyStudio.Rhino
{
    /// <summary>
    /// Reads rhino/.env (copied next to the plug-in at build time) or %APPDATA%\FamilyStudio\.env,
    /// and reports what is configured. Values are never displayed or logged, only their presence.
    /// </summary>
    internal sealed class EnvStatus
    {
        private static readonly Regex Assignment = new Regex(@"^(?:export\s+)?(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>.*)$");

        public string? LoadedFile { get; private set; }
        public string CodexHome { get; private set; } = "";
        public bool ChatGptSignedIn { get; private set; }

        public static EnvStatus Load()
        {
            var status = new EnvStatus();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pluginFolder = Path.GetDirectoryName(typeof(EnvStatus).Assembly.Location) ?? "";
            var candidates = new[]
            {
                Path.Combine(pluginFolder, ".env"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FamilyStudio", ".env")
            };
            foreach (var file in candidates)
            {
                if (!File.Exists(file)) continue;
                status.LoadedFile = file;
                foreach (var raw in File.ReadAllLines(file))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    var match = Assignment.Match(line);
                    if (!match.Success) continue;
                    var value = match.Groups["value"].Value.Trim().Trim('"', '\'');
                    var comment = value.IndexOf(" #", StringComparison.Ordinal);
                    if (comment >= 0) value = value.Substring(0, comment).TrimEnd();
                    if (value.Length > 0) values[match.Groups["key"].Value] = value;
                }
                break;
            }

            string? Get(string key) =>
                Environment.GetEnvironmentVariable(key) is string fromProcess && fromProcess.Length > 0 ? fromProcess
                : values.TryGetValue(key, out var fromFile) ? fromFile : null;

            // The same default as the Revit plug-in, so one ChatGPT sign-in serves both.
            var home = Get("OPENAI_CODEX_HOME");
            status.CodexHome = home is null
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FamilyStudio", "codex-home")
                : Environment.ExpandEnvironmentVariables(home);
            status.ChatGptSignedIn = File.Exists(Path.Combine(status.CodexHome, "auth.json"));
            return status;
        }
    }
}
