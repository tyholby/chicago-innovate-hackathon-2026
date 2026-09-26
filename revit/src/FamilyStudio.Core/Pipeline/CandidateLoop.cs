using System.Diagnostics;
using System.Text.Json;
using FamilyStudio.Core.Json;
using FamilyStudio.Core.Model;
using FamilyStudio.Core.Prompts;
using FamilyStudio.Core.Validation;

namespace FamilyStudio.Core.Pipeline;

/// <summary>
/// Runs a planning stage, validates what comes back, and asks for up to two targeted corrections.
/// The model never touches Revit: a candidate is only accepted once the host has validated it.
/// When a merge function is supplied, corrections are patches pinned to the rejected candidate's
/// hash, so the model changes only what failed instead of rewriting everything.
/// </summary>
public static class CandidateLoop
{
    public const int MaxCorrections = 2;

    public static async Task<T> RunAsync<T>(
        IStudioAgent agent,
        SessionJournal journal,
        StageRequest request,
        Func<T, T> validate,
        CancellationToken cancellationToken,
        JsonElement? patchSchema = null,
        Func<T, T, T>? merge = null)
    {
        var watch = Stopwatch.StartNew();
        journal.Artifact($"{request.Name}-prompt.txt", request.Prompt);
        var feedback = "";
        var hasRejected = false;
        T? rejected = default;

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = request.Timeout - watch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"The {request.Name} step ran out of time while correcting its result.");

            var expectPatch = hasRejected && merge is not null && patchSchema is not null;
            var result = await agent.RunAsync(request with
            {
                Prompt = request.Prompt + feedback,
                Timeout = remaining,
                OutputSchema = expectPatch ? patchSchema : request.OutputSchema
            }, cancellationToken).ConfigureAwait(false);

            try
            {
                T candidate;
                if (expectPatch)
                {
                    var patch = StudioJson.Read<CandidatePatch<T>>(result.Text);
                    if (patch.BaseSha256 != StudioJson.Hash(rejected))
                        throw StudioValidationException.Single("stale_patch", "baseSha256", "Use the exact baseSha256 supplied with the rejected result.");
                    candidate = merge!(rejected!, patch.Changes);
                }
                else candidate = StudioJson.Read<T>(result.Text);

                rejected = candidate;
                hasRejected = true;
                var accepted = validate(candidate);
                journal.Write("candidate_accepted", new { request.Name, result.StageId, attempt });
                journal.Artifact($"{request.Name}-accepted.json", StudioJson.Write(accepted));
                return accepted;
            }
            catch (Exception error) when (error is ArgumentException or JsonException)
            {
                IReadOnlyList<ValidationIssue> issues = error is StudioValidationException known
                    ? known.Issues
                    : new[] { new ValidationIssue(error is JsonException ? "invalid_json" : "invalid_candidate", null, null, null, "result", null, null, null, error.Message) };
                journal.Write("candidate_rejected", new { request.Name, result.StageId, attempt, issues });
                if (attempt >= MaxCorrections)
                    throw new InvalidOperationException($"The {request.Name} step still failed validation after {MaxCorrections} corrections: {error.Message}", error);
                var usePatch = hasRejected && merge is not null && patchSchema is not null;
                feedback = StudioPrompts.Correction(issues, usePatch ? StudioJson.Hash(rejected) : null,
                    hasRejected ? StudioJson.Write(rejected) : result.Text);
            }
        }
    }
}
