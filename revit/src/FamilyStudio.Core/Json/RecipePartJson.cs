using System.Text.Json;
using System.Text.Json.Serialization;
using FamilyStudio.Core.Model;

namespace FamilyStudio.Core.Json;

/// <summary>
/// The JSON form of a recipe part, shared by the output schema, prompts, corrections, artifacts and hashes.
/// A part lists only the fields its shape uses, in schema order, and points are [x, y, z] arrays: the model
/// writes every field it is given, so each field left out is output it no longer spends time on. Reading
/// also accepts the earlier form (every field, points as {x, y, z} objects).
/// </summary>
public sealed class RecipePartJsonConverter : JsonConverter<RecipePart>
{
    /// <summary>The fields every part writes, shape first.</summary>
    public static readonly string[] CommonFields = { "shape", "name", "materialId", "componentId", "isFloorSupport", "mirror" };

    /// <summary>The fields each shape adds after <see cref="CommonFields"/>, in order.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ShapeFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [PartShapes.Box] = new[] { "minM", "maxM", "tiltDegrees", "radiusM" },
        [PartShapes.Cylinder] = new[] { "pointsM", "radiusM", "endRadiusM" },
        [PartShapes.Sphere] = new[] { "pointsM", "radiusM" },
        [PartShapes.Tube] = new[] { "pointsM", "radiusM" },
        [PartShapes.Profile] = new[] { "plane", "outlineM", "fromM", "toM", "radiusM" }
    };

    private static readonly string[] AllFields = CommonFields.Concat(ShapeFields.Values.SelectMany(f => f)).Distinct(StringComparer.Ordinal).ToArray();

    public override RecipePart Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var part = document.RootElement;
        if (part.ValueKind != JsonValueKind.Object) throw new JsonException("Each part must be a JSON object.");
        foreach (var field in part.EnumerateObject())
            if (!AllFields.Contains(field.Name, StringComparer.Ordinal))
                throw new JsonException($"A part has no field named \"{field.Name}\".");

        // Fields a shape does not use are absent; validation reports anything a shape is missing.
        return new RecipePart(
            Name: Text(part, "name")!,
            MinM: Point(part, "minM"),
            MaxM: Point(part, "maxM"),
            TiltDegrees: Number(part, "tiltDegrees") ?? 0,
            MaterialId: Text(part, "materialId")!,
            ComponentId: Text(part, "componentId"),
            IsFloorSupport: Flag(part, "isFloorSupport") ?? false,
            Shape: Text(part, "shape") ?? PartShapes.Box,
            RadiusM: Number(part, "radiusM") ?? 0,
            EndRadiusM: Number(part, "endRadiusM"),
            PointsM: Field(part, "pointsM") is JsonElement points ? List(points, "pointsM").Select(p => ToPoint(p, "pointsM")).ToArray() : null,
            Plane: Text(part, "plane"),
            OutlineM: Field(part, "outlineM") is JsonElement outline ? List(outline, "outlineM").Select(p => Numbers(p, "outlineM")).ToArray() : null,
            FromM: Number(part, "fromM"),
            ToM: Number(part, "toM"),
            Mirror: Flag(part, "mirror") ?? false);
    }

    public override void Write(Utf8JsonWriter writer, RecipePart part, JsonSerializerOptions options)
    {
        // An unknown shape writes everything, so a rejected part is shown back to the model in full.
        var fields = part.Shape is not null && ShapeFields.TryGetValue(part.Shape, out var own) ? CommonFields.Concat(own) : AllFields;
        writer.WriteStartObject();
        foreach (var field in fields) WriteField(writer, part, field);
        writer.WriteEndObject();
    }

    private static void WriteField(Utf8JsonWriter writer, RecipePart part, string field)
    {
        switch (field)
        {
            case "shape": WriteText(writer, field, part.Shape); break;
            case "name": WriteText(writer, field, part.Name); break;
            case "materialId": WriteText(writer, field, part.MaterialId); break;
            case "componentId": WriteText(writer, field, part.ComponentId); break;
            case "isFloorSupport": writer.WriteBoolean(field, part.IsFloorSupport); break;
            case "mirror": writer.WriteBoolean(field, part.Mirror); break;
            case "minM": WritePoint(writer, field, part.MinM); break;
            case "maxM": WritePoint(writer, field, part.MaxM); break;
            case "tiltDegrees": writer.WriteNumber(field, part.TiltDegrees); break;
            case "radiusM": writer.WriteNumber(field, part.RadiusM); break;
            case "endRadiusM": WriteNumber(writer, field, part.EndRadiusM); break;
            case "pointsM":
                if (part.PointsM is null) { writer.WriteNull(field); break; }
                writer.WriteStartArray(field);
                foreach (var point in part.PointsM) WritePoint(writer, null, point);
                writer.WriteEndArray();
                break;
            case "plane": WriteText(writer, field, part.Plane); break;
            case "outlineM":
                if (part.OutlineM is null) { writer.WriteNull(field); break; }
                writer.WriteStartArray(field);
                foreach (var point in part.OutlineM)
                {
                    if (point is null) { writer.WriteNullValue(); continue; }
                    writer.WriteStartArray();
                    foreach (var value in point) writer.WriteNumberValue(value);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                break;
            case "fromM": WriteNumber(writer, field, part.FromM); break;
            case "toM": WriteNumber(writer, field, part.ToM); break;
            default: throw new InvalidOperationException($"No JSON form for part field {field}.");
        }
    }

    // ---- reading ------------------------------------------------------------------------------

    private static JsonElement? Field(JsonElement part, string name) =>
        part.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static string? Text(JsonElement part, string name) => Field(part, name) switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        _ => throw new JsonException($"A part's {name} must be a string.")
    };

    private static double? Number(JsonElement part, string name) => Field(part, name) switch
    {
        null => null,
        { ValueKind: JsonValueKind.Number } value => value.GetDouble(),
        _ => throw new JsonException($"A part's {name} must be a number.")
    };

    private static bool? Flag(JsonElement part, string name) => Field(part, name) switch
    {
        null => null,
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => throw new JsonException($"A part's {name} must be true or false.")
    };

    private static Vec3? Point(JsonElement part, string name) => Field(part, name) is JsonElement value ? ToPoint(value, name) : null;

    private static IEnumerable<JsonElement> List(JsonElement value, string name) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray()
        : throw new JsonException($"A part's {name} must be a list.");

    private static double[] Numbers(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number)
            ? value.EnumerateArray().Select(v => v.GetDouble()).ToArray()
            : throw new JsonException($"Each entry of a part's {name} must be a list of numbers.");

    private static Vec3 ToPoint(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object) // the earlier {x, y, z} form
            return value.Deserialize<Vec3>(StudioJson.Options) ?? throw new JsonException($"A point in a part's {name} is empty.");
        var coordinates = value.ValueKind == JsonValueKind.Array ? Numbers(value, name) : Array.Empty<double>();
        return coordinates.Length == 3
            ? new Vec3(coordinates[0], coordinates[1], coordinates[2])
            : throw new JsonException($"Each point in a part's {name} is [x, y, z]: three numbers in metres.");
    }

    // ---- writing ------------------------------------------------------------------------------

    private static void WriteText(Utf8JsonWriter writer, string field, string? value)
    {
        if (value is null) writer.WriteNull(field);
        else writer.WriteString(field, value);
    }

    private static void WriteNumber(Utf8JsonWriter writer, string field, double? value)
    {
        if (value is double number) writer.WriteNumber(field, number);
        else writer.WriteNull(field);
    }

    private static void WritePoint(Utf8JsonWriter writer, string? field, Vec3? point)
    {
        if (point is null)
        {
            if (field is null) writer.WriteNullValue();
            else writer.WriteNull(field);
            return;
        }
        if (field is null) writer.WriteStartArray();
        else writer.WriteStartArray(field);
        writer.WriteNumberValue(point.X);
        writer.WriteNumberValue(point.Y);
        writer.WriteNumberValue(point.Z);
        writer.WriteEndArray();
    }
}
