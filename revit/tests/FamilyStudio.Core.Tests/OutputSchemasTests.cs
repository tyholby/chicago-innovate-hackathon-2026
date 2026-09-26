using System.Text.Json;
using FamilyStudio.Core.Json;
using FamilyStudio.Core.Model;

namespace FamilyStudio.Core.Tests;

public class OutputSchemasTests
{
    private static readonly StudioBrief Brief = Samples.CollectionBrief();

    public static IEnumerable<object[]> Schemas() => new[]
    {
        new object[] { "brief", OutputSchemas.Brief() },
        new object[] { "recipe", OutputSchemas.Recipe(Brief, Brief.Assets[0]) },
        new object[] { "recipe patch", OutputSchemas.RecipePatch(Brief, Brief.Assets[0]) },
        new object[] { "layout", OutputSchemas.Layout(Brief) },
        new object[] { "layout patch", OutputSchemas.LayoutPatch(Brief) },
        new object[] { "review", OutputSchemas.Review(Brief) },
        new object[] { "repair", OutputSchemas.Repair(Brief) }
    };

    [Theory]
    [MemberData(nameof(Schemas))]
    public void Every_object_is_strict(string name, JsonElement schema)
    {
        foreach (var node in Objects(schema))
        {
            var properties = node.GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
            var required = node.GetProperty("required").EnumerateArray().Select(r => r.GetString()).OrderBy(n => n).ToArray();
            Assert.True(properties.SequenceEqual(required), $"{name}: every property must be required");
            Assert.False(node.GetProperty("additionalProperties").GetBoolean(), $"{name}: additionalProperties must be false");
        }
    }

    [Fact]
    public void Brief_schema_matches_the_record_shape()
    {
        var sample = StudioJson.Element(Samples.StoolBrief());
        AssertSameShape(OutputSchemas.Brief(), sample);
    }

    [Fact]
    public void Recipe_schema_matches_the_record_shape_and_narrows_ids()
    {
        var brief = Samples.StoolBrief();
        var schema = OutputSchemas.Recipe(brief, brief.Assets[0]);
        AssertSameShape(schema, StudioJson.Element(new RecipeDraft("a1", Samples.StoolParts())));
        var assetIds = schema.GetProperty("properties").GetProperty("assetId").GetProperty("enum").EnumerateArray().Select(e => e.GetString());
        Assert.Equal(new[] { "a1" }, assetIds);
    }

    [Fact]
    public void Recipe_schema_covers_every_shape()
    {
        var brief = Samples.ChairBrief();
        AssertSameShape(OutputSchemas.Recipe(brief, brief.Assets[0]), StudioJson.Element(new RecipeDraft("a1", Samples.ChairParts())));
    }

    [Fact]
    public void Review_and_repair_schemas_match_their_records()
    {
        AssertSameShape(OutputSchemas.Review(Brief), StudioJson.Element(new ReviewReport(false, "s",
            new[] { new ReviewFinding("a1", "a1-1", "scale", "major", "e", "c") })));
        AssertSameShape(OutputSchemas.Repair(Brief), StudioJson.Element(new SceneRepair("h",
            new[] { new RecipeEdit("a1", Samples.StoolParts(), new[] { "x" }) },
            new[] { new PlacementIntent("k", "a1", "mirror", Vec3.Zero, 0, "r", "s", "x", 0, "f", Vec3.Zero) })));
    }

    [Fact]
    public void A_schema_valid_brief_round_trips_through_the_records()
    {
        var json = StudioJson.Write(Samples.StoolBrief());
        Assert.Equal(json, StudioJson.Write(StudioJson.Read<StudioBrief>(json)));
    }

    [Fact]
    public void Unexpected_properties_are_rejected_when_reading()
    {
        var json = StudioJson.Write(new RecipeDraft("a1", Samples.StoolParts())).Replace("\"assetId\"", "\"type\": \"json_object\", \"assetId\"");
        Assert.Throws<JsonException>(() => StudioJson.Read<RecipeDraft>(json));
    }

    [Fact]
    public void A_surrounding_code_fence_is_tolerated()
    {
        var json = "```json\n" + StudioJson.Write(new RecipeDraft("a1", Samples.StoolParts())) + "\n```";
        Assert.Equal("a1", StudioJson.Read<RecipeDraft>(json).AssetId);
    }

    private static IEnumerable<JsonElement> Objects(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) yield break;
        if (node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object")
            yield return node;
        foreach (var property in node.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
                foreach (var child in Objects(property.Value)) yield return child;
            if (property.Value.ValueKind == JsonValueKind.Array)
                foreach (var item in property.Value.EnumerateArray())
                foreach (var child in Objects(item)) yield return child;
        }
    }

    /// <summary>Walks a sample value and its schema together; every JSON property must exist in the schema and vice versa.</summary>
    private static void AssertSameShape(JsonElement schema, JsonElement value, string path = "$")
    {
        if (schema.TryGetProperty("anyOf", out var anyOf))
        {
            if (value.ValueKind == JsonValueKind.Null) return;
            var options = anyOf.EnumerateArray().Where(s => s.GetProperty("type").GetString() != "null").ToArray();
            // A part picks its branch by shape; a nullable value has exactly one other branch.
            schema = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("shape", out var shape)
                ? options.Single(s => s.GetProperty("properties").GetProperty("shape").GetProperty("enum")[0].GetString() == shape.GetString())
                : options.Single();
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var schemaProps = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
                var valueProps = value.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
                Assert.True(schemaProps.SequenceEqual(valueProps), $"{path}: schema [{string.Join(",", schemaProps)}] vs value [{string.Join(",", valueProps)}]");
                foreach (var p in value.EnumerateObject())
                    AssertSameShape(schema.GetProperty("properties").GetProperty(p.Name), p.Value, $"{path}.{p.Name}");
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) AssertSameShape(schema.GetProperty("items"), item, path + "[]");
                break;
        }
    }
}
