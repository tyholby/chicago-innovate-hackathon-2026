using System.Text.Json;
using FamilyStudio.Core.Json;
using FamilyStudio.Core.Model;

namespace FamilyStudio.Core.Tests;

public class RecipePartJsonTests
{
    [Fact]
    public void Each_shape_writes_only_its_own_fields()
    {
        foreach (var part in Samples.ChairParts())
        {
            var names = StudioJson.Element(part).EnumerateObject().Select(p => p.Name);
            Assert.Equal(RecipePartJsonConverter.CommonFields.Concat(RecipePartJsonConverter.ShapeFields[part.Shape]), names);
        }
    }

    [Fact]
    public void Points_and_corners_are_xyz_arrays()
    {
        var parts = Samples.ChairParts();
        var box = StudioJson.Element(parts.First(p => p.Shape == PartShapes.Box));
        Assert.Equal(new[] { -0.33, -0.34, 0.28 }, box.GetProperty("minM").EnumerateArray().Select(v => v.GetDouble()));
        var tube = StudioJson.Element(parts.First(p => p.Shape == PartShapes.Tube));
        Assert.All(tube.GetProperty("pointsM").EnumerateArray(), point => Assert.Equal(3, point.GetArrayLength()));
    }

    [Fact]
    public void A_recipe_round_trips_through_its_json()
    {
        var json = StudioJson.WriteCompact(new RecipeDraft("a1", Samples.ChairParts()));
        Assert.Equal(json, StudioJson.WriteCompact(StudioJson.Read<RecipeDraft>(json)));
    }

    [Fact]
    public void Compact_json_is_one_line()
    {
        var json = StudioJson.WriteCompact(new RecipeDraft("a1", Samples.ChairParts()));
        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain(": ", json);
    }

    [Fact]
    public void The_earlier_every_field_form_still_reads()
    {
        const string json = """
            {"name":"leg","minM":null,"maxM":null,"tiltDegrees":0,"materialId":"m1","componentId":null,"isFloorSupport":true,
             "shape":"cylinder","radiusM":0.014,"endRadiusM":0.02,"pointsM":[{"x":-0.33,"y":-0.36,"z":0},{"x":-0.3,"y":-0.32,"z":0.3}],
             "plane":null,"outlineM":null,"fromM":null,"toM":null,"mirror":true}
            """;
        var part = StudioJson.Read<RecipePart>(json);
        Assert.Equal(PartShapes.Cylinder, part.Shape);
        Assert.Equal(new Vec3(-0.3, -0.32, 0.3), part.PointsM![1]);
        Assert.Equal(0.02, part.EndRadiusM);
        Assert.True(part.Mirror && part.IsFloorSupport);
    }

    [Fact]
    public void Unknown_part_fields_are_rejected()
    {
        const string json = """{"shape":"sphere","name":"knob","materialId":"m1","componentId":null,"isFloorSupport":false,"mirror":false,"pointsM":[[0,0,0.5]],"radiusM":0.01,"color":"red"}""";
        Assert.Throws<JsonException>(() => StudioJson.Read<RecipePart>(json));
    }

    [Fact]
    public void A_point_must_be_three_numbers()
    {
        const string json = """{"shape":"sphere","name":"knob","materialId":"m1","componentId":null,"isFloorSupport":false,"mirror":false,"pointsM":[[0,0]],"radiusM":0.01}""";
        Assert.Throws<JsonException>(() => StudioJson.Read<RecipePart>(json));
    }
}
