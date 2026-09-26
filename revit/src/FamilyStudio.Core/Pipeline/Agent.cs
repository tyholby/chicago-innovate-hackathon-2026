using System.Text.Json;

namespace FamilyStudio.Core.Pipeline;

public enum StageKind { Reasoning, Image }

/// <summary>A model and reasoning effort from the signed-in account's Codex catalog.</summary>
public sealed record ModelChoice(string Model, string Effort)
{
    public override string ToString() => $"{Model} / {Effort}";
}

/// <summary>
/// One stage: a fresh thread with one message. Image paths are attached as local images.
/// A reasoning stage with an output schema must answer with JSON matching it. When set,
/// <c>CountKey</c> names a JSON key whose occurrences in the streamed answer are counted for
/// progress ("shape" appears once per recipe part).
/// </summary>
public sealed record StageRequest(
    string Name,
    StageKind Kind,
    ModelChoice Model,
    string Instructions,
    string Prompt,
    IReadOnlyList<string> Images,
    JsonElement? OutputSchema,
    TimeSpan Timeout,
    string? CountKey = null);

public enum StagePhase { Starting, Thinking, Writing, Drawing, Finished }

/// <summary>
/// What one running stage is doing: its phase, how long it has run, how much of its answer has
/// streamed in, and how often the request's count key has appeared. <c>Note</c> carries a passing
/// message such as a reconnect. <c>Finished</c> is the last report for a stage ID.
/// </summary>
public sealed record StageProgress(string StageName, string StageId, StagePhase Phase, TimeSpan Elapsed, int CharactersWritten, int Counted, string? Note = null);

public sealed record GeneratedImage(string ItemId, string SavedPath, string? RevisedPrompt);

public sealed record StageUsage(long? InputTokens, long? CachedInputTokens, long? OutputTokens, long? ReasoningTokens, long? TotalTokens);

public sealed record StageResult(
    string StageId,
    string Text,
    GeneratedImage? Image,
    StageUsage? Usage,
    string? ActualModel,
    TimeSpan Elapsed);

/// <summary>
/// Runs stages against a model provider; several may run at once. The Codex implementation is
/// <c>CodexService</c>.
/// </summary>
public interface IStudioAgent : IDisposable
{
    /// <summary>Live progress of each running stage, about once a second and at every phase change. May fire on any thread.</summary>
    event Action<StageProgress>? Progress;

    Task<StageResult> RunAsync(StageRequest request, CancellationToken cancellationToken);
}
