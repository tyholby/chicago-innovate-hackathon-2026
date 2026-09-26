namespace FamilyStudio.Core.Model;

/// <summary>A resolved instance position. Rotation is degrees about world Z; zero faces negative Y.</summary>
public sealed record Placement(
    string Key,
    string AssetId,
    Vec3 PositionM,
    double RotationDegrees,
    string? SupportKey,
    string? SupportPartName);

public static class PlacementModes
{
    public const string Absolute = "absolute";
    public const string Relative = "relative";
    public const string Surface = "surface";
    public const string Mirror = "mirror";
    public static readonly string[] All = { Absolute, Relative, Surface, Mirror };
}

/// <summary>
/// How the model describes an arrangement: intent, not coordinates. "The lamp stands on the
/// bedside table's top", "the second chair mirrors the first across X = 0". The host resolves
/// intents into positions, so support heights and facing angles are computed, never guessed.
/// </summary>
public sealed record PlacementIntent(
    string Key,
    string AssetId,
    string Mode,
    Vec3 OffsetM,
    double RotationDegrees,
    string? ReferenceKey,
    string? SupportPartName,
    string? MirrorAxis,
    double? MirrorPlaneM,
    string? FacingKey,
    Vec3? FacingPointM)
{
    public static PlacementIntent AbsoluteFrom(Placement placement) => new(placement.Key, placement.AssetId,
        PlacementModes.Absolute, placement.PositionM, placement.RotationDegrees, null, null, null, null, null, null);
}

public sealed record PlacementIntentPlan(PlacementIntent[] Placements)
{
    public static PlacementIntentPlan Merge(PlacementIntentPlan before, PlacementIntentPlan changes) =>
        before.Placements is null || changes.Placements is null
            ? throw new ArgumentException("Placements are required.")
            : new(Named.Merge(before.Placements, changes.Placements, p => p.Key));
}

/// <summary>Everything the native host needs to build or update the preview room.</summary>
public sealed record BuildProposal(FamilyRecipe[] Recipes, Placement[] Placements)
{
    public static BuildProposal Merge(BuildProposal? before, BuildProposal update) => before is null
        ? update
        : new(Named.Merge(before.Recipes, update.Recipes, r => r.AssetId),
            Named.Merge(before.Placements, update.Placements, p => p.Key));
}
