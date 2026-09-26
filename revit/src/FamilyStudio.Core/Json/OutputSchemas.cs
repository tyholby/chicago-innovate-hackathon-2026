using System.Text.Json;
using System.Text.Json.Nodes;
using FamilyStudio.Core.Model;

namespace FamilyStudio.Core.Json;

/// <summary>
/// Strict JSON Schemas for every structured model output. Written by hand rather than generated,
/// for two reasons: they must satisfy strict structured-output rules (every property required,
/// no extra properties, nullability spelled out), and they can be narrowed per request, so a
/// recipe for item a3 can only name a3 and only the brief's own material IDs.
/// </summary>
public static class OutputSchemas
{
    public static JsonElement Brief() => Finish(Object(
        ("title", String()),
        ("style", String()),
        ("assets", Array(Object(
            ("id", String()),
            ("name", String()),
            ("description", String()),
            ("quantity", Integer()),
            ("sizeM", Vec3()),
            ("floorStanding", Boolean()),
            ("components", Array(Object(
                ("id", String()),
                ("description", String()),
                ("sizeM", Vec3())))),
            ("dimensionsConfirmed", Boolean())))),
        ("materials", Array(Object(
            ("id", String()),
            ("name", String()),
            ("description", String()),
            ("rgb", Array(Integer())))))));

    /// <summary>A full recipe for one item, with IDs narrowed to that item's brief.</summary>
    public static JsonElement Recipe(StudioBrief brief, AssetBrief asset) => Finish(RecipeNode(brief, asset));

    /// <summary>A correction to a rejected recipe: the base hash plus only the changed parts.</summary>
    public static JsonElement RecipePatch(StudioBrief brief, AssetBrief asset) =>
        Finish(Object(("baseSha256", String()), ("changes", RecipeNode(brief, asset))));

    public static JsonElement Layout(StudioBrief brief) => Finish(LayoutNode(brief));

    public static JsonElement LayoutPatch(StudioBrief brief) =>
        Finish(Object(("baseSha256", String()), ("changes", LayoutNode(brief))));

    public static JsonElement Review(StudioBrief brief) => Finish(Object(
        ("passed", Boolean()),
        ("summary", String()),
        ("findings", Array(Object(
            ("assetId", Enum(AssetIds(brief))),
            ("placementKey", Nullable(String())),
            ("category", Enum(ReviewVocabulary.Categories)),
            ("severity", Enum(ReviewVocabulary.Severities)),
            ("evidence", String()),
            ("correction", String()))))));

    public static JsonElement Repair(StudioBrief brief) => Finish(Object(
        ("baseSha256", String()),
        ("recipes", Array(Object(
            ("assetId", Enum(AssetIds(brief))),
            ("upsertParts", Array(PartNode(brief, null))),
            ("removeParts", Array(String()))))),
        ("placements", Array(IntentNode(brief)))));

    private static JsonObject RecipeNode(StudioBrief brief, AssetBrief asset) => Object(
        ("assetId", Enum(new[] { asset.Id })),
        ("parts", Array(PartNode(brief, asset))));

    private static JsonObject PartNode(StudioBrief brief, AssetBrief? asset)
    {
        var components = asset?.Components.Select(c => c.Id).ToArray()
            ?? brief.Assets.SelectMany(a => a.Components.Select(c => c.Id)).Distinct(StringComparer.Ordinal).ToArray();
        // Shape first, so the model decides what a part is before it writes that shape's fields.
        return Object(
            ("name", String()),
            ("shape", Enum(PartShapes.All)),
            ("materialId", Enum(brief.Materials.Select(m => m.Id))),
            ("componentId", components.Length == 0 ? Null() : Nullable(Enum(components))),
            ("isFloorSupport", Boolean()),
            ("mirror", Boolean()),
            ("minM", Nullable(Vec3())),
            ("maxM", Nullable(Vec3())),
            ("tiltDegrees", Number()),
            ("radiusM", Number()),
            ("endRadiusM", Nullable(Number())),
            ("pointsM", Nullable(Array(Vec3()))),
            ("plane", Nullable(Enum(ProfilePlanes.All))),
            ("outlineM", Nullable(Array(Array(Number())))),
            ("fromM", Nullable(Number())),
            ("toM", Nullable(Number())));
    }

    private static JsonObject LayoutNode(StudioBrief brief) => Object(("placements", Array(IntentNode(brief))));

    private static JsonObject IntentNode(StudioBrief brief) => Object(
        ("key", String()),
        ("assetId", Enum(AssetIds(brief))),
        ("mode", Enum(PlacementModes.All)),
        ("offsetM", Vec3()),
        ("rotationDegrees", Number()),
        ("referenceKey", Nullable(String())),
        ("supportPartName", Nullable(String())),
        ("mirrorAxis", Nullable(Enum(new[] { "x", "y" }))),
        ("mirrorPlaneM", Nullable(Number())),
        ("facingKey", Nullable(String())),
        ("facingPointM", Nullable(Vec3())));

    private static IEnumerable<string> AssetIds(StudioBrief brief) => brief.Assets.Select(a => a.Id);

    // ---- tiny schema vocabulary ----------------------------------------------------------

    private static JsonObject Object(params (string Name, JsonNode Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties) props[name] = schema;
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray()),
            ["additionalProperties"] = false
        };
    }

    private static JsonObject Array(JsonNode items) => new() { ["type"] = "array", ["items"] = items };
    private static JsonObject String() => new() { ["type"] = "string" };
    private static JsonObject Number() => new() { ["type"] = "number" };
    private static JsonObject Integer() => new() { ["type"] = "integer" };
    private static JsonObject Boolean() => new() { ["type"] = "boolean" };
    private static JsonObject Null() => new() { ["type"] = "null" };

    private static JsonObject Enum(IEnumerable<string> values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())
    };

    private static JsonObject Nullable(JsonNode schema) => new() { ["anyOf"] = new JsonArray(schema, Null()) };

    private static JsonObject Vec3() => Object(("x", Number()), ("y", Number()), ("z", Number()));

    private static JsonElement Finish(JsonObject schema) => JsonSerializer.SerializeToElement(schema);
}
