using System.Globalization;

namespace FamilyStudio.Core.Pipeline;

/// <summary>Turns live stage progress into the window's detail line.</summary>
public static class StageProgressText
{
    /// <summary>
    /// One running stage reads as a sentence ("Writing, 23 parts so far (4:10)"). Several read as a list
    /// labelled by item ("Desk: writing 12 parts  ·  Chair: thinking 1:05"), in the order given.
    /// </summary>
    public static string Describe(IReadOnlyList<StageProgress> stages, Func<string, string?> label) => stages.Count switch
    {
        0 => "",
        1 => stages[0].Note ?? Sentence(stages[0]),
        _ => string.Join("  ·  ", stages.Select(s => $"{label(s.StageName) ?? s.StageName}: {s.Note ?? Phrase(s)}"))
    };

    private static string Sentence(StageProgress s) => s.Phase switch
    {
        StagePhase.Thinking => $"Thinking ({Clock(s.Elapsed)})",
        StagePhase.Writing => $"Writing, {Written(s)} so far ({Clock(s.Elapsed)})",
        StagePhase.Drawing => $"Drawing the reference image ({Clock(s.Elapsed)})",
        _ => $"Starting ({Clock(s.Elapsed)})"
    };

    private static string Phrase(StageProgress s) => s.Phase switch
    {
        StagePhase.Thinking => $"thinking {Clock(s.Elapsed)}",
        StagePhase.Writing => $"writing {Written(s)}",
        StagePhase.Drawing => $"drawing {Clock(s.Elapsed)}",
        _ => "starting"
    };

    /// <summary>Counted units where the stage has them (parts, placements), otherwise the size written so far.</summary>
    private static string Written(StageProgress s) => Unit(s.StageName) is string unit && s.Counted > 0
        ? $"{s.Counted} {unit}{(s.Counted == 1 ? "" : "s")}"
        : (s.CharactersWritten / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";

    private static string? Unit(string stageName) =>
        stageName.StartsWith("recipe-", StringComparison.Ordinal) || stageName.StartsWith("refine-", StringComparison.Ordinal) || stageName == "repair" ? "part"
        : stageName == "layout" ? "placement"
        : null;

    private static string Clock(TimeSpan elapsed) => $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
}
