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
/// A reasoning stage with an output schema must answer with JSON matching it.
/// </summary>
public sealed record StageRequest(
    string Name,
    StageKind Kind,
    ModelChoice Model,
    string Instructions,
    string Prompt,
    IReadOnlyList<string> Images,
    JsonElement? OutputSchema,
    TimeSpan Timeout);

public sealed record GeneratedImage(string ItemId, string SavedPath, string? RevisedPrompt);

public sealed record StageUsage(long? InputTokens, long? CachedInputTokens, long? OutputTokens, long? ReasoningTokens, long? TotalTokens);

public sealed record StageResult(
    string StageId,
    string Text,
    GeneratedImage? Image,
    StageUsage? Usage,
    string? ActualModel,
    TimeSpan Elapsed);

/// <summary>Runs stages against a model provider. The Codex implementation is <c>CodexService</c>.</summary>
public interface IStudioAgent : IDisposable
{
    /// <summary>Short, human-readable progress lines ("Reconnecting to ChatGPT...").</summary>
    event Action<string>? Progress;

    Task<StageResult> RunAsync(StageRequest request, CancellationToken cancellationToken);
}
