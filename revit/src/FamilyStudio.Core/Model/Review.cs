namespace FamilyStudio.Core.Model;

public static class ReviewVocabulary
{
    public static readonly string[] Categories = { "count", "silhouette", "facing", "scale", "support", "placement" };
    public static readonly string[] Severities = { "major", "minor" };
}

public sealed record ReviewFinding(
    string AssetId,
    string? PlacementKey,
    string Category,
    string Severity,
    string Evidence,
    string Correction);

/// <summary>An independent visual check of the built families against the accepted brief and reference.</summary>
public sealed record ReviewReport(bool Passed, string Summary, ReviewFinding[] Findings)
{
    public void Validate(StudioBrief brief)
    {
        if (string.IsNullOrWhiteSpace(Summary) || Findings is null)
            throw new ArgumentException("A review needs a summary and a findings list.");
        foreach (var finding in Findings)
        {
            if (!brief.Assets.Any(a => a.Id == finding.AssetId) ||
                !ReviewVocabulary.Categories.Contains(finding.Category) ||
                !ReviewVocabulary.Severities.Contains(finding.Severity) ||
                string.IsNullOrWhiteSpace(finding.Evidence) || string.IsNullOrWhiteSpace(finding.Correction))
                throw new ArgumentException("A review finding is incomplete or names an unknown item.");
        }
        if (Passed && Findings.Any(f => f.Severity == "major"))
            throw new ArgumentException("A review with a major finding cannot pass.");
    }
}

/// <summary>A targeted recipe edit: upsert named parts and remove named parts. Everything else is kept.</summary>
public sealed record RecipeEdit(string AssetId, RecipePart[] UpsertParts, string[] RemoveParts);

/// <summary>A repair that only lists what changes, pinned to the exact scene it was written against.</summary>
public sealed record SceneRepair(string BaseSha256, RecipeEdit[] Recipes, PlacementIntent[] Placements);

/// <summary>A correction to a rejected candidate, pinned to the exact candidate it corrects.</summary>
public sealed record CandidatePatch<T>(string BaseSha256, T Changes);
