using System.Text;
using System.Text.Json;
using FamilyStudio.Core.Json;

namespace FamilyStudio.Core.Pipeline;

/// <summary>
/// A per-session folder with an append-only JSONL event log and named artifacts (prompts, briefs,
/// recipes, images, receipts). It never records credentials, and image bytes live in files rather
/// than log lines.
/// </summary>
public sealed class SessionJournal
{
    private readonly object _gate = new();

    public string DirectoryPath { get; }

    public SessionJournal(string directory)
    {
        DirectoryPath = directory;
        Directory.CreateDirectory(directory);
    }

    /// <summary>Creates a new session folder under the output root, grouped by day.</summary>
    public static SessionJournal CreateUnder(string root) =>
        new(Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd"), DateTime.Now.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]));

    public void Write(string kind, object data)
    {
        var line = JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, kind, data }, StudioJson.Compact);
        lock (_gate) File.AppendAllText(Path.Combine(DirectoryPath, "events.jsonl"), line + "\n", new UTF8Encoding(false));
    }

    public string Artifact(string fileName, string text)
    {
        if (Path.GetFileName(fileName) != fileName) throw new ArgumentException("Artifact names must be plain file names.");
        var path = Path.Combine(DirectoryPath, fileName);
        lock (_gate) File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    public string PathFor(params string[] parts)
    {
        var path = Path.Combine(new[] { DirectoryPath }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
