using FamilyStudio.Core.Model;

namespace FamilyStudio.Core.Validation;

/// <summary>
/// Turns placement intents into world positions. A deliberately small grammar (absolute, relative,
/// surface, mirror, plus optional facing), resolved recursively with cycle detection. There is no
/// solver and no guessing: an intent that cannot be resolved exactly is reported back as an issue.
/// </summary>
public sealed class PlacementResolver
{
    private readonly Dictionary<string, PlacementIntent> _intents;
    private readonly Dictionary<string, FamilyRecipe> _recipes;
    private readonly Dictionary<string, Vec3> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _angles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);

    public PlacementResolver(PlacementIntentPlan plan, IEnumerable<FamilyRecipe> recipes)
    {
        if (plan.Placements is null || plan.Placements.Any(p => p is null) ||
            plan.Placements.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() != plan.Placements.Length)
            throw StudioValidationException.Single("duplicate_key", "placements.key", "Every placement needs a unique key.");
        _intents = plan.Placements.ToDictionary(p => p.Key, StringComparer.Ordinal);
        _recipes = recipes.ToDictionary(r => r.AssetId, StringComparer.Ordinal);

        var issues = new List<ValidationIssue>();
        foreach (var p in plan.Placements) issues.AddRange(Check(p));
        if (issues.Count > 0) throw new StudioValidationException(issues);
    }

    public Placement[] Resolve()
    {
        var placements = new List<Placement>();
        var issues = new List<ValidationIssue>();
        foreach (var intent in _intents.Values)
        {
            try
            {
                if (intent.Mode == PlacementModes.Mirror && _intents[intent.ReferenceKey!].AssetId != intent.AssetId)
                    throw Problem(intent, "mirror_identity", "assetId", "A mirrored instance must be the same item as the one it mirrors.");
                var (supportKey, supportPart) = SupportOf(intent);
                placements.Add(new(intent.Key, intent.AssetId, PositionOf(intent.Key), AngleOf(intent.Key), supportKey, supportPart));
            }
            catch (StudioValidationException e) { issues.AddRange(e.Issues); }
        }
        if (issues.Count > 0)
            throw new StudioValidationException(issues.GroupBy(i => (i.Code, i.PlacementKey, i.Property)).Select(g => g.First()));
        return placements.ToArray();
    }

    private IEnumerable<ValidationIssue> Check(PlacementIntent p)
    {
        if (string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 64 || !_recipes.ContainsKey(p.AssetId) ||
            p.OffsetM is not { IsFinite: true } || !double.IsFinite(p.RotationDegrees) || p.FacingPointM is { IsFinite: false })
            yield return Issue(p, "invalid_intent", "key/assetId/offsetM", "Use a short key, a built item ID and finite metre coordinates.");
        if (!PlacementModes.All.Contains(p.Mode))
            yield return Issue(p, "unsupported_mode", "mode", "Mode must be absolute, relative, surface or mirror.");
        if ((p.Mode == PlacementModes.Absolute) != (p.ReferenceKey is null) || p.ReferenceKey == p.Key ||
            (p.ReferenceKey is not null && !_intents.ContainsKey(p.ReferenceKey)))
            yield return Issue(p, "invalid_reference", "referenceKey", "Absolute placements have no reference; every other mode names another existing placement.");
        if ((p.Mode == PlacementModes.Surface) != (p.SupportPartName is not null) ||
            (p.Mode == PlacementModes.Surface && p.OffsetM is not null && Math.Abs(p.OffsetM.Z) > 1e-9))
            yield return Issue(p, "invalid_surface", "supportPartName/offsetM.z", "Surface placements name a horizontal support part and use a zero Z offset.");
        var mirrorValid = p.Mode == PlacementModes.Mirror
            ? p.MirrorAxis is "x" or "y" && p.MirrorPlaneM is double plane && double.IsFinite(plane) &&
              p.OffsetM == Vec3.Zero && p.RotationDegrees == 0
            : p.MirrorAxis is null && p.MirrorPlaneM is null;
        if (!mirrorValid)
            yield return Issue(p, "invalid_mirror", "mirrorAxis/mirrorPlaneM", "Mirror placements use axis x or y, a finite plane, zero offset and zero rotation; other modes leave mirror fields null.");
        if (p.FacingKey == p.Key || (p.FacingKey is not null && !_intents.ContainsKey(p.FacingKey)) ||
            (p.FacingKey is not null && p.FacingPointM is not null) ||
            ((p.FacingKey is not null || p.FacingPointM is not null) && p.RotationDegrees != 0))
            yield return Issue(p, "invalid_facing", "facingKey/facingPointM", "Face one other placement or one point, with rotationDegrees zero.");
    }

    private (string? Key, string? Part) SupportOf(PlacementIntent p) => p.Mode switch
    {
        PlacementModes.Surface => (p.ReferenceKey, p.SupportPartName),
        PlacementModes.Mirror => Guard("support:" + p.Key, p, () => SupportOf(_intents[p.ReferenceKey!])),
        _ => (null, null)
    };

    private Vec3 PositionOf(string key)
    {
        if (_positions.TryGetValue(key, out var cached)) return cached;
        var p = _intents[key];
        var position = Guard("position:" + key, p, () =>
        {
            if (p.Mode == PlacementModes.Absolute) return p.OffsetM;
            var anchor = PositionOf(p.ReferenceKey!);
            if (p.Mode == PlacementModes.Mirror)
                return p.MirrorAxis == "x"
                    ? anchor with { X = 2 * p.MirrorPlaneM!.Value - anchor.X }
                    : anchor with { Y = 2 * p.MirrorPlaneM!.Value - anchor.Y };

            // Relative X/Y follow the anchor's own axes. Relative Z is a world elevation;
            // surface Z is always measured from the named support part.
            var shifted = PlacementRules.Rotate(p.OffsetM, AngleOf(p.ReferenceKey!));
            var z = p.Mode == PlacementModes.Surface
                ? anchor.Z + PlacementRules.Surface(_recipes[_intents[p.ReferenceKey!].AssetId], p.SupportPartName!).MaxM.Z
                  - RecipeRules.Bounds(_recipes[p.AssetId].Parts).Min.Z
                : p.OffsetM.Z;
            return new Vec3(anchor.X + shifted.X, anchor.Y + shifted.Y, z);
        });
        _positions[key] = position;
        return position;
    }

    private double AngleOf(string key)
    {
        if (_angles.TryGetValue(key, out var cached)) return cached;
        var p = _intents[key];
        var angle = Guard("angle:" + key, p, () =>
        {
            var target = p.FacingKey is not null ? PositionOf(p.FacingKey) : p.FacingPointM;
            if (target is not null)
            {
                var here = PositionOf(key);
                var dx = target.X - here.X;
                var dy = target.Y - here.Y;
                if (Math.Abs(dx) + Math.Abs(dy) < 1e-8)
                    throw Problem(p, "coincident_facing", "facingKey/facingPointM", "The facing target is at the same position as the instance.");
                // Rotation zero faces -Y, so the heading toward (dx, dy) is atan2(dx, -dy).
                return Math.Atan2(dx, -dy) * 180 / Math.PI;
            }
            if (p.Mode == PlacementModes.Mirror)
                return p.MirrorAxis == "x" ? -AngleOf(p.ReferenceKey!) : 180 - AngleOf(p.ReferenceKey!);
            return p.RotationDegrees + (p.Mode is PlacementModes.Relative or PlacementModes.Surface ? AngleOf(p.ReferenceKey!) : 0);
        });
        angle = ((angle % 360) + 360) % 360;
        _angles[key] = angle;
        return angle;
    }

    private T Guard<T>(string step, PlacementIntent p, Func<T> resolve)
    {
        if (!_resolving.Add(step))
            throw Problem(p, "dependency_cycle", "referenceKey/facingKey", "Placements refer to each other in a cycle.");
        try { return resolve(); }
        finally { _resolving.Remove(step); }
    }

    private static ValidationIssue Issue(PlacementIntent p, string code, string property, string message) =>
        new(code, p.AssetId, p.Key, p.SupportPartName, property, null, null, null, message);

    private static StudioValidationException Problem(PlacementIntent p, string code, string property, string message) =>
        new(new[] { Issue(p, code, property, message) });
}
