using FamilyStudio.Core.Model;
using FamilyStudio.Core.Validation;

namespace FamilyStudio.Core.Tests;

public class PlacementTests
{
    private static PlacementIntent Intent(string key, string asset, string mode, Vec3 offset, double rotation = 0,
        string? reference = null, string? support = null, string? mirrorAxis = null, double? plane = null,
        string? facingKey = null, Vec3? facingPoint = null) =>
        new(key, asset, mode, offset, rotation, reference, support, mirrorAxis, plane, facingKey, facingPoint);

    private static (StudioBrief Brief, FamilyRecipe[] Recipes) Room()
    {
        var brief = Samples.CollectionBrief();
        var recipes = new List<FamilyRecipe> { Samples.Table(brief) };
        for (var i = 2; i <= 7; i++) recipes.Add(Samples.Box($"a{i}", new Vec3(0.5, 0.5, 0.5), brief, floor: i != 2));
        return (brief, recipes.ToArray());
    }

    [Fact]
    public void Relative_offsets_follow_the_anchor_rotation()
    {
        var (_, recipes) = Room();
        var plan = new PlacementIntentPlan(new[]
        {
            Intent("table", "a1", "absolute", new Vec3(1, 1, 0), 90),
            Intent("chair", "a3", "relative", new Vec3(0, -1, 0), reference: "table")
        });
        var placed = new PlacementResolver(plan, recipes).Resolve().ToDictionary(p => p.Key);
        // Rotating (0, -1) by 90 degrees gives (1, 0).
        Assert.Equal(2, placed["chair"].PositionM.X, 6);
        Assert.Equal(1, placed["chair"].PositionM.Y, 6);
        Assert.Equal(90, placed["chair"].RotationDegrees, 6);
    }

    [Fact]
    public void Surface_placements_derive_their_height_from_the_named_part()
    {
        var (_, recipes) = Room();
        var plan = new PlacementIntentPlan(new[]
        {
            Intent("table", "a1", "absolute", Vec3.Zero),
            Intent("lamp", "a2", "surface", new Vec3(0.3, 0, 0), reference: "table", support: "top")
        });
        var lamp = new PlacementResolver(plan, recipes).Resolve().Single(p => p.Key == "lamp");
        Assert.Equal(0.75, lamp.PositionM.Z, 6);
        Assert.Equal("table", lamp.SupportKey);
        Assert.Equal("top", lamp.SupportPartName);
    }

    [Fact]
    public void Mirrors_reflect_position_and_heading()
    {
        var (_, recipes) = Room();
        var plan = new PlacementIntentPlan(new[]
        {
            Intent("left", "a3", "absolute", new Vec3(-1.5, 0.5, 0), 30),
            Intent("right", "a3", "mirror", Vec3.Zero, reference: "left", mirrorAxis: "x", plane: 0)
        });
        var right = new PlacementResolver(plan, recipes).Resolve().Single(p => p.Key == "right");
        Assert.Equal(1.5, right.PositionM.X, 6);
        Assert.Equal(0.5, right.PositionM.Y, 6);
        Assert.Equal(330, right.RotationDegrees, 6);
    }

    [Fact]
    public void Facing_aims_the_front_at_the_target()
    {
        var (_, recipes) = Room();
        var plan = new PlacementIntentPlan(new[]
        {
            Intent("table", "a1", "absolute", Vec3.Zero),
            Intent("chair", "a3", "absolute", new Vec3(0, -1.5, 0), facingKey: "table")
        });
        // A chair south of the table faces +Y (north), which is 180 degrees from the default -Y front.
        var chair = new PlacementResolver(plan, recipes).Resolve().Single(p => p.Key == "chair");
        Assert.Equal(180, chair.RotationDegrees, 6);
    }

    [Fact]
    public void Cycles_are_reported_not_followed()
    {
        var (_, recipes) = Room();
        var plan = new PlacementIntentPlan(new[]
        {
            Intent("a", "a3", "relative", new Vec3(1, 0, 0), reference: "b"),
            Intent("b", "a4", "relative", new Vec3(1, 0, 0), reference: "a")
        });
        var error = Assert.Throws<StudioValidationException>(() => new PlacementResolver(plan, recipes).Resolve());
        Assert.Contains(error.Issues, i => i.Code == "dependency_cycle");
    }

    [Fact]
    public void Invalid_intents_are_all_reported_before_resolution()
    {
        var (_, recipes) = Room();
        var plan = new PlacementIntentPlan(new[]
        {
            Intent("x", "a3", "teleport", Vec3.Zero),
            Intent("y", "a4", "absolute", Vec3.Zero, reference: "x"),
            Intent("z", "a5", "mirror", new Vec3(1, 0, 0), reference: "x", mirrorAxis: "x", plane: 0)
        });
        var error = Assert.Throws<StudioValidationException>(() => new PlacementResolver(plan, recipes));
        Assert.Contains(error.Issues, i => i.Code == "unsupported_mode");
        Assert.Contains(error.Issues, i => i.Code == "invalid_reference");
        Assert.Contains(error.Issues, i => i.Code == "invalid_mirror");
    }

    [Fact]
    public void Rules_catch_floating_items_and_items_outside_the_room()
    {
        var (brief, recipes) = Room();
        var placements = new List<Placement>
        {
            new("a1-1", "a1", new Vec3(0, 0, 0), 0, null, null),
            new("a2-1", "a2", new Vec3(0, 0, 0.75), 0, "a1-1", "top"),
            new("a3-1", "a3", new Vec3(3.9, 0, 0), 0, null, null),   // hangs over the room edge
            new("a4-1", "a4", new Vec3(-2, 1, 0.2), 0, null, null)   // floats above the floor
        };
        placements.AddRange(new[] { "a5", "a6", "a7" }.Select((a, i) => new Placement($"{a}-1", a, new Vec3(-3 + i, -2, 0), 0, null, null)));
        var error = Assert.Throws<StudioValidationException>(() => PlacementRules.Validate(new BuildProposal(recipes, placements.ToArray()), brief));
        Assert.Contains(error.Issues, i => i.Code == "room_containment" && i.PlacementKey == "a3-1");
        Assert.Contains(error.Issues, i => i.Code == "floor_contact" && i.PlacementKey == "a4-1");
        Assert.DoesNotContain(error.Issues, i => i.PlacementKey == "a2-1");
    }

    [Fact]
    public void A_base_that_overhangs_its_support_is_rejected()
    {
        var (brief, recipes) = Room();
        var placements = new[]
        {
            new Placement("a1-1", "a1", Vec3.Zero, 0, null, null),
            new Placement("a2-1", "a2", new Vec3(0.5, 0, 0.75), 0, "a1-1", "top") // 0.5 m box centred 0.5 m out on a 0.6 m half-width top
        }.Concat(new[] { "a3", "a4", "a5", "a6", "a7" }.Select((a, i) => new Placement($"{a}-1", a, new Vec3(-3 + i, -2, 0), 0, null, null))).ToArray();
        var error = Assert.Throws<StudioValidationException>(() => PlacementRules.Validate(new BuildProposal(recipes, placements), brief));
        Assert.Contains(error.Issues, i => i.Code == "support_footprint" && i.PlacementKey == "a2-1");
    }

    [Fact]
    public void Resolved_placements_can_be_turned_back_into_intents()
    {
        var (brief, recipes) = Room();
        var placements = new[]
        {
            new Placement("a1-1", "a1", new Vec3(1, 0, 0), 90, null, null),
            new Placement("a2-1", "a2", new Vec3(1, 0.2, 0.75), 90, "a1-1", "top")
        }.Concat(new[] { "a3", "a4", "a5", "a6", "a7" }.Select((a, i) => new Placement($"{a}-1", a, new Vec3(-3 + i, -2, 0), 0, null, null))).ToArray();
        var intents = PlacementRules.ToIntents(new BuildProposal(recipes, placements));
        var round = new PlacementResolver(intents, recipes).Resolve().ToDictionary(p => p.Key);
        Assert.Equal(1, round["a2-1"].PositionM.X, 6);
        Assert.Equal(0.2, round["a2-1"].PositionM.Y, 6);
        Assert.Equal(0.75, round["a2-1"].PositionM.Z, 6);
        Assert.Equal(90, round["a2-1"].RotationDegrees, 6);
    }
}
