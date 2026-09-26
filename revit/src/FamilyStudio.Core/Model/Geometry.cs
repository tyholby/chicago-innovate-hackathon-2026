using System.Text.Json.Serialization;
using FamilyStudio.Core.Json;
using FamilyStudio.Core.Validation;

namespace FamilyStudio.Core.Model;

/// <summary>
/// One part of a family, in family coordinates (metres, origin at the centre of the base, front
/// toward -Y). <see cref="Shape"/> says which fields apply:
/// <list type="bullet">
/// <item><c>box</c>: <see cref="MinM"/> to <see cref="MaxM"/>, tilted by <see cref="TiltDegrees"/> about its own
/// X axis through its centre, with all twelve edges rounded by <see cref="RadiusM"/> (0 for crisp edges).</item>
/// <item><c>cylinder</c>: the axis from <see cref="PointsM"/>[0] to [1], radius <see cref="RadiusM"/> at the
/// first point and <see cref="EndRadiusM"/> at the second (a taper when they differ).</item>
/// <item><c>sphere</c>: centre <see cref="PointsM"/>[0], radius <see cref="RadiusM"/>.</item>
/// <item><c>tube</c>: a round bar of radius <see cref="RadiusM"/> through 2 to 12 <see cref="PointsM"/>, with
/// rounded joints and ends.</item>
/// <item><c>profile</c>: an outline in <see cref="Plane"/> (<see cref="OutlineM"/>, corners rounded by
/// <see cref="RadiusM"/>) extruded along the remaining axis from <see cref="FromM"/> to <see cref="ToM"/>.</item>
/// </list>
/// <see cref="Mirror"/> adds the part's mirror image across X = 0. In JSON a part lists only the fields
/// of its shape, with [x, y, z] points (<see cref="RecipePartJsonConverter"/>).
/// </summary>
[JsonConverter(typeof(RecipePartJsonConverter))]
public sealed record RecipePart(
    string Name,
    Vec3? MinM,
    Vec3? MaxM,
    double TiltDegrees,
    string MaterialId,
    string? ComponentId,
    bool IsFloorSupport,
    string Shape = PartShapes.Box,
    double RadiusM = 0,
    double? EndRadiusM = null,
    Vec3[]? PointsM = null,
    string? Plane = null,
    double[][]? OutlineM = null,
    double? FromM = null,
    double? ToM = null,
    bool Mirror = false);

/// <summary>The shapes a part can take.</summary>
public static class PartShapes
{
    public const string Box = "box";
    public const string Cylinder = "cylinder";
    public const string Sphere = "sphere";
    public const string Tube = "tube";
    public const string Profile = "profile";
    public static readonly string[] All = { Box, Cylinder, Sphere, Tube, Profile };
}

/// <summary>
/// Where a profile's outline is drawn. Its [u, v] points map to (X, Z) for front, (Y, Z) for side and
/// (X, Y) for plan, and it is extruded along the remaining axis.
/// </summary>
public static class ProfilePlanes
{
    public const string Front = "front";
    public const string Side = "side";
    public const string Plan = "plan";
    public static readonly string[] All = { Front, Side, Plan };
}

/// <summary>An axis-aligned bounding box in metres.</summary>
public sealed record Box3(Vec3 Min, Vec3 Max)
{
    public Vec3 Size => new(Max.X - Min.X, Max.Y - Min.Y, Max.Z - Min.Z);

    /// <summary>This box grown by <paramref name="distance"/> on every side.</summary>
    public Box3 Grow(double distance) =>
        new(new(Min.X - distance, Min.Y - distance, Min.Z - distance), new(Max.X + distance, Max.Y + distance, Max.Z + distance));

    public Box3 Union(Box3 other) =>
        new(new(Math.Min(Min.X, other.Min.X), Math.Min(Min.Y, other.Min.Y), Math.Min(Min.Z, other.Min.Z)),
            new(Math.Max(Max.X, other.Max.X), Math.Max(Max.Y, other.Max.Y), Math.Max(Max.Z, other.Max.Z)));

    public static Box3 Of(IEnumerable<Vec3> points)
    {
        var all = points.ToArray();
        if (all.Length == 0) throw new ArgumentException("A bounding box needs at least one point.");
        return new(new(all.Min(p => p.X), all.Min(p => p.Y), all.Min(p => p.Z)),
            new(all.Max(p => p.X), all.Max(p => p.Y), all.Max(p => p.Z)));
    }
}

/// <summary>
/// What a planning stage returns: the parts of one family. The host computes the envelope itself
/// from the exact geometry, so the model never has to do (and can never get wrong) the arithmetic
/// of tilted corners, circles and rounded edges.
/// </summary>
public sealed record RecipeDraft(string AssetId, RecipePart[] Parts)
{
    public FamilyRecipe Compile(StudioBrief brief)
    {
        if (Parts is null || Parts.Length < 1 || Parts.Any(p => p is null))
            throw new ArgumentException("A recipe needs at least one part.");
        RecipeRules.ValidateShapes(Parts, brief, AssetId);
        var parts = Parts.Select(Shapes.Settle).ToArray();
        var bounds = RecipeRules.Bounds(parts);
        var recipe = new FamilyRecipe(AssetId, bounds.Min, bounds.Max, parts);
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

/// <summary>
/// A validated recipe with its host-measured envelope. <see cref="Parts"/> are as designed, with
/// mirrored parts listed once; <see cref="Solids"/> lists every part the native build creates.
/// </summary>
public sealed record FamilyRecipe(string AssetId, Vec3 EnvelopeMinM, Vec3 EnvelopeMaxM, RecipePart[] Parts)
{
    [JsonIgnore] public IEnumerable<RecipePart> Solids => Shapes.Expand(Parts);
}

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
