using FamilyStudio.Core.Model;
using FamilyStudio.Core.Prompts;

namespace FamilyStudio.Core.Tests;

public class PresetsTests
{
    [Fact]
    public void The_window_opens_on_a_complete_example_item()
    {
        var draft = Presets.Create(Presets.SingleId);
        Assert.True(draft.IsSingleItem);
        Assert.True(draft.IsComplete, "the example can run as is");
        Assert.Contains("bas-relief medallion", draft.Assets[0]);
        // Every field is filled, matching the example photo that ships with the add-in.
        Assert.Equal("Trident Roundle", draft.AssetNames[0]);
        Assert.Equal(new[] { "Stone" }, draft.Materials);
        // 48 x 5 x 48 in, shown in inches as whole numbers.
        var preset = Presets.Get(Presets.SingleId);
        Assert.Equal(LengthUnit.Inches, preset.Unit);
        var size = draft.KnownSizeM!;
        Assert.Equal(new[] { "48", "5", "48" }, new[] { size.X, size.Y, size.Z }.Select(m => Dimensions.Format(m, preset.Unit)));
        Assert.Equal(1219.2, size.X * 1000, 6);
        Assert.Equal(127, size.Y * 1000, 6);
    }

    [Fact]
    public void Collections_use_millimetres_and_unknown_ids_fall_back_to_the_example()
    {
        Assert.All(Presets.All.Where(p => !p.Draft.IsSingleItem), p => Assert.Equal(LengthUnit.Millimetres, p.Unit));
        Assert.Same(Presets.Get(Presets.SingleId), Presets.Get("no-such-preset"));
    }

    [Fact]
    public void Example_collections_are_complete_and_the_blank_one_is_empty()
    {
        foreach (var preset in Presets.All.Where(p => !p.Draft.IsSingleItem))
        {
            var draft = Presets.Create(preset.Id);
            if (preset.Id == "blank") Assert.False(draft.HasContent);
            else Assert.True(draft.IsComplete, preset.Id);
        }
    }
}
