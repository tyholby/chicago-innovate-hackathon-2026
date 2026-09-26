using FamilyStudio.Core.Validation;

namespace FamilyStudio.Core.Model;

/// <summary>
/// One solid in a family: an axis-aligned box from <see cref="MinM"/> to <see cref="MaxM"/>, optionally
/// tilted about its own X axis through its centre (a reclined backrest, a sloped lid).
/// </summary>
public sealed record RecipePart(
    string Name,
    Vec3 MinM,
    Vec3 MaxM,
    double TiltDegrees,
    string MaterialId,
    string? ComponentId,
    bool IsFloorSupport);

/// <summary>An axis-aligned bounding box in metres.</summary>
public sealed record Box3(Vec3 Min, Vec3 Max)
{
    public Vec3 Size => new(Max.X - Min.X, Max.Y - Min.Y, Max.Z - Min.Z);

    public static Box3 Of(IEnumerable<Vec3> points)
    {
        var all = points.ToArray();
        if (all.Length == 0) throw new ArgumentException("A bounding box needs at least one point.");
        return new(new(all.Min(p => p.X), all.Min(p => p.Y), all.Min(p => p.Z)),
            new(all.Max(p => p.X), all.Max(p => p.Y), all.Max(p => p.Z)));
    }
}

/// <summary>
/// What a planning stage returns: the parts of one family. The host computes the envelope itself,
/// so the model never has to do (and can never get wrong) the tilted-corner arithmetic.
/// </summary>
public sealed record RecipeDraft(string AssetId, RecipePart[] Parts)
{
    public FamilyRecipe Compile(StudioBrief brief)
    {
        if (Parts is null || Parts.Length is < 1 or > StudioLimits.MaxParts ||
            Parts.Any(p => p is null || p.MinM is null || p.MaxM is null || !p.MinM.IsFinite || !p.MaxM.IsFinite || !double.IsFinite(p.TiltDegrees)))
            throw new ArgumentException($"A recipe needs 1 to {StudioLimits.MaxParts} parts with finite coordinates.");
        var bounds = RecipeRules.Bounds(Parts);
        var recipe = new FamilyRecipe(AssetId, bounds.Min, bounds.Max, Parts);
        RecipeRules.Validate(recipe, brief);
        return recipe;
    }

    /// <summary>Applies a correction that names only the parts it changes. Unnamed parts are kept.</summary>
    public static RecipeDraft Merge(RecipeDraft before, RecipeDraft changes)
    {
        if (before.AssetId != changes.AssetId || before.Parts is null || changes.Parts is null)
            throw new ArgumentException("A correction must keep the same item and supply a part list.");
        return new(before.AssetId, Named.Merge(before.Parts, changes.Parts, p => p.Name));
    }
}

/// <summary>A validated recipe with its host-measured envelope.</summary>
public sealed record FamilyRecipe(string AssetId, Vec3 EnvelopeMinM, Vec3 EnvelopeMaxM, RecipePart[] Parts);

internal static class Named
{
    /// <summary>Replaces items with the same key in place and appends new ones, keeping the original order.</summary>
    public static T[] Merge<T>(T[] before, T[] changes, Func<T, string> key)
    {
        if (changes.Select(key).Distinct(StringComparer.Ordinal).Count() != changes.Length)
            throw new ArgumentException("A correction names the same entry twice.");
        var replacements = changes.ToDictionary(key, StringComparer.Ordinal);
        var merged = new List<T>(before.Length + changes.Length);
        foreach (var item in before)
            merged.Add(replacements.Remove(key(item), out var replacement) ? replacement : item);
        merged.AddRange(changes.Where(c => replacements.ContainsKey(key(c))));
        return merged.ToArray();
    }
}
