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
        Assert.Contains("lounge chair", draft.Assets[0]);
        // Every field is filled, matching the brief of the example photo that ships with the add-in.
        Assert.Equal("Walnut Lounge Chair", draft.AssetNames[0]);
        Assert.Equal(new Vec3(0.76, 0.86, 0.80), draft.KnownSizeM);
        Assert.Equal(2, draft.Materials.Length);
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
