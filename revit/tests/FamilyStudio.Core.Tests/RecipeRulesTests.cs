using FamilyStudio.Core.Model;
using FamilyStudio.Core.Validation;

namespace FamilyStudio.Core.Tests;

public class RecipeRulesTests
{
    [Fact]
    public void A_valid_recipe_compiles_with_its_measured_envelope()
    {
        var recipe = Samples.StoolRecipe();
        Assert.Equal(new Vec3(-0.3, -0.25, 0), recipe.EnvelopeMinM);
        Assert.Equal(new Vec3(0.3, 0.25, 0.75), recipe.EnvelopeMaxM);
    }

    [Fact]
    public void Positive_tilt_swings_the_top_of_a_part_toward_the_front()
    {
        // A 1 m tall slab centred on the origin, tilted +90 degrees, lies along -Y at the top end.
        var part = new RecipePart("slab", new Vec3(0, -0.05, 0), new Vec3(0.1, 0.05, 1), 90, "m1", null, false);
        var bounds = RecipeRules.Bounds(new[] { part });
        Assert.Equal(-0.5, bounds.Min.Y, 6);
        Assert.Equal(0.5, bounds.Max.Y, 6);
        Assert.Equal(0.45, bounds.Min.Z, 6);
        Assert.Equal(0.55, bounds.Max.Z, 6);
    }

    [Fact]
    public void Tilted_corners_count_toward_the_envelope()
    {
        var parts = Samples.StoolParts().Append(new RecipePart("back", new Vec3(-0.3, 0.2, 0.75), new Vec3(0.3, 0.25, 1.1), -15, "m2", null, false)).ToArray();
        var bounds = RecipeRules.Bounds(parts);
        Assert.True(bounds.Max.Y > 0.25, "a backrest leaning back extends behind its untilted box");
    }

    [Fact]
    public void Estimated_dimensions_allow_five_percent_but_not_more()
    {
        var brief = Samples.StoolBrief();
        new RecipeDraft("a1", Samples.StoolParts(height: 0.78)).Compile(brief); // +30 mm on 750 mm is inside 37.5 mm

        var error = Assert.Throws<StudioValidationException>(() => new RecipeDraft("a1", Samples.StoolParts(height: 0.80)).Compile(brief));
        var issue = Assert.Single(error.Issues);
        Assert.Equal("dimension_mismatch", issue.Code);
        Assert.Equal("sizeM.z", issue.Property);
    }

    [Fact]
    public void Confirmed_dimensions_allow_only_two_millimetres()
    {
        var brief = Samples.StoolBrief(confirmed: true);
        new RecipeDraft("a1", Samples.StoolParts(height: 0.7515)).Compile(brief);
        Assert.Throws<StudioValidationException>(() => new RecipeDraft("a1", Samples.StoolParts(height: 0.755)).Compile(brief));
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var parts = Samples.StoolParts().Select(p => p.Name == "seat" ? p with { MaterialId = "m9" } : p with { IsFloorSupport = false }).ToArray();
        var error = Assert.Throws<StudioValidationException>(() => new RecipeDraft("a1", parts).Compile(Samples.StoolBrief()));
        Assert.Contains(error.Issues, i => i.Code == "unknown_material");
    }

    [Fact]
    public void Floor_standing_items_need_marked_supports()
    {
        var parts = Samples.StoolParts().Select(p => p with { IsFloorSupport = false }).ToArray();
        var error = Assert.Throws<StudioValidationException>(() => new RecipeDraft("a1", parts).Compile(Samples.StoolBrief()));
        Assert.Contains(error.Issues, i => i.Code == "floor_contact");
    }

    [Fact]
    public void A_floor_support_must_touch_zero()
    {
        var parts = Samples.StoolParts().Select(p => p.Name == "leg-fl" ? p with { MinM = p.MinM with { Z = 0.01 } } : p).ToArray();
        var error = Assert.Throws<StudioValidationException>(() => new RecipeDraft("a1", parts).Compile(Samples.StoolBrief()));
        Assert.Contains(error.Issues, i => i.Code == "floor_support" && i.PartName == "leg-fl");
    }

    [Fact]
    public void Sized_components_are_checked_on_their_own()
    {
        var brief = Samples.StoolBrief() with
        {
            Assets = new[] { Samples.StoolBrief().Assets[0] with { Components = new[] { new ComponentBrief("seat", "Seat slab", new Vec3(0.6, 0.5, 0.05)) } } }
        };
        var untagged = Assert.Throws<StudioValidationException>(() => new RecipeDraft("a1", Samples.StoolParts()).Compile(brief));
        Assert.Contains(untagged.Issues, i => i.Code == "missing_component");

        var tagged = Samples.StoolParts().Select(p => p.Name == "seat" ? p with { ComponentId = "seat" } : p).ToArray();
        new RecipeDraft("a1", tagged).Compile(brief);
    }

    [Fact]
    public void Corrections_replace_named_parts_and_keep_the_rest_in_order()
    {
        var before = new RecipeDraft("a1", Samples.StoolParts());
        var seat = before.Parts[0] with { MaxM = before.Parts[0].MaxM with { Z = 0.76 } };
        var extra = before.Parts[1] with { Name = "stretcher" };
        var merged = RecipeDraft.Merge(before, new RecipeDraft("a1", new[] { seat, extra }));
        Assert.Equal(new[] { "seat", "leg-fl", "leg-fr", "leg-bl", "leg-br", "stretcher" }, merged.Parts.Select(p => p.Name));
        Assert.Equal(0.76, merged.Parts[0].MaxM.Z);
    }
}
