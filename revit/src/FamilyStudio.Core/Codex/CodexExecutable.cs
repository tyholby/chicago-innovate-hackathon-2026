using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FamilyStudio.Core.Codex;

/// <summary>A Codex CLI binary this plugin can drive, and where it was found.</summary>
public sealed partial record CodexExecutable(string Path, string Version, string Source)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Finds Codex in this order: an explicit path from the .env file, PATH, then the usual install
    /// locations of the Codex desktop app, the npm package and the ChatGPT desktop app. Among
    /// install locations the newest version wins.
    /// </summary>
    public static async Task<CodexExecutable> FindAsync(string? explicitPath, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var path = explicitPath.Trim().Trim('"');
            return await ProbeAsync(path, ".env (OPENAI_CODEX_PATH)", cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException($"OPENAI_CODEX_PATH points to \"{path}\", which is not a working Codex executable.");
        }

        foreach (var candidate in PathCandidates())
            if (await ProbeAsync(candidate, "PATH", cancellationToken).ConfigureAwait(false) is { } onPath)
                return onPath;

        var found = new List<CodexExecutable>();
        foreach (var (candidate, source) in InstallCandidates())
            if (await ProbeAsync(candidate, source, cancellationToken).ConfigureAwait(false) is { } installed)
                found.Add(installed);
        return found.OrderByDescending(c => c.Version, Comparer<string>.Create(CompareVersions)).FirstOrDefault()
            ?? throw new FileNotFoundException(
                "Codex was not found. Install the Codex app or the Codex CLI (npm install -g @openai/codex), " +
                "or set OPENAI_CODEX_PATH in the .env file.");
    }

    private static IEnumerable<string> PathCandidates()
    {
        var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "codex.exe" : "codex";
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(folder))
                yield return System.IO.Path.Combine(folder.Trim().Trim('"'), name);
    }

    private static IEnumerable<(string Path, string Source)> InstallCandidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var path in Search(System.IO.Path.Combine(local, "OpenAI", "Codex", "bin"), "codex.exe", depth: 1))
                yield return (path, "Codex app");
            // npm's codex.cmd shim cannot be launched directly; its native binary lives in the package.
            foreach (var path in Search(System.IO.Path.Combine(roaming, "npm", "node_modules", "@openai", "codex", "vendor"), "codex.exe", depth: 3))
                yield return (path, "npm package");
            foreach (var path in Search(System.IO.Path.Combine(local, "Programs", "ChatGPT"), "codex.exe", depth: 3))
                yield return (path, "ChatGPT app");
        }
        else
        {
            yield return ("/Applications/Codex.app/Contents/Resources/codex", "Codex app");
            yield return ("/Applications/ChatGPT.app/Contents/Resources/codex", "ChatGPT app");
            yield return ("/opt/homebrew/bin/codex", "Homebrew");
            yield return ("/usr/local/bin/codex", "/usr/local/bin");
        }
    }

    private static string[] Search(string root, string fileName, int depth)
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(root, fileName, new EnumerationOptions
            {
                RecurseSubdirectories = depth > 0,
                MaxRecursionDepth = depth,
                IgnoreInaccessible = true
            }).ToArray();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static async Task<CodexExecutable?> ProbeAsync(string path, string source, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var output = await RunAsync(path, new[] { "--version" }, null, cancellationToken).ConfigureAwait(false);
        var match = output is null ? Match.Empty : VersionPattern().Match(output);
        return match.Success ? new CodexExecutable(System.IO.Path.GetFullPath(path), match.Value, source) : null;
    }

    /// <summary>Runs a short Codex subcommand and returns its stdout, or null if it failed or hung.</summary>
    internal static async Task<string?> RunAsync(string path, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProbeTimeout);
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment) info.Environment[key] = value;
        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return process.ExitCode == 0 ? await stdout.ConfigureAwait(false) : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }

    /// <summary>Semantic-version comparison: 0.154.0 > 0.154.0-alpha.6 > 0.153.9.</summary>
    public static int CompareVersions(string left, string right)
    {
        static (System.Version Core, string? Pre) Parse(string value)
        {
            var parts = value.Split('+')[0].Split('-', 2);
            return (System.Version.TryParse(parts[0], out var core) ? core : new System.Version(0, 0), parts.Length == 2 ? parts[1] : null);
        }
        var a = Parse(left);
        var b = Parse(right);
        var result = a.Core.CompareTo(b.Core);
        if (result != 0) return result;
        if (a.Pre is null || b.Pre is null) return a.Pre is null ? (b.Pre is null ? 0 : 1) : -1;
        var ap = a.Pre.Split('.');
        var bp = b.Pre.Split('.');
        for (var i = 0; i < Math.Min(ap.Length, bp.Length); i++)
        {
            var an = long.TryParse(ap[i], out var av);
            var bn = long.TryParse(bp[i], out var bv);
            result = an && bn ? av.CompareTo(bv) : an ? -1 : bn ? 1 : string.CompareOrdinal(ap[i], bp[i]);
            if (result != 0) return result;
        }
        return ap.Length.CompareTo(bp.Length);
    }

    [GeneratedRegex(@"\b\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\b")]
    private static partial Regex VersionPattern();
}
