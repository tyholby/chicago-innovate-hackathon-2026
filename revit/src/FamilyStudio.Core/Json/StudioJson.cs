using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FamilyStudio.Core.Json;

/// <summary>One JSON convention for prompts, model outputs, artifacts and the journal.</summary>
public static class StudioJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    /// <summary>Compact output for wire messages and journal lines.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    /// <summary>Lenient reading for protocol messages whose shape may grow between Codex versions.</summary>
    public static readonly JsonSerializerOptions Lenient = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(StripFence(json), Options)
        ?? throw new JsonException("Expected a JSON object.");

    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    /// <summary>Stable SHA-256 of a value's canonical JSON, used to pin corrections to exact bases.</summary>
    public static string Hash<T>(T value) => Sha256(Write(value));

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Removes one surrounding Markdown code fence, which some models add around JSON.</summary>
    internal static string StripFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal) || !trimmed.EndsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0) return trimmed;
        return trimmed[(firstLineEnd + 1)..^3].Trim();
    }
}

/// <summary>Small helpers for reading loosely shaped protocol JSON without exceptions.</summary>
public static class JsonFields
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static JsonElement? Opt(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

    public static bool True(this JsonElement e, string name) => e.Opt(name)?.ValueKind == JsonValueKind.True;

    public static long? Long(this JsonElement e, string name) =>
        e.Opt(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : null;

    public static IEnumerable<JsonElement> Items(this JsonElement e, string name) =>
        e.Opt(name) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    /// <summary>Codex reports usage-limit resets as Unix seconds in "resetsAt".</summary>
    public static DateTimeOffset? ResetsAt(this JsonElement e) =>
        e.Long("resetsAt") is long seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
