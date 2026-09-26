using System.Globalization;
using System.Text.RegularExpressions;

namespace FamilyStudio.Core.Model;

public enum LengthUnit { Millimetres, Inches }

/// <summary>
/// Parsing and formatting of the overall dimensions a user types. Accepts plain numbers in the
/// selected unit, explicit suffixes (650 mm, 65 cm, 0.65 m, 25.5 in, 25.5") and architectural
/// feet-and-inches (2'-6", 2' 6 1/2", 3').
/// </summary>
public static partial class Dimensions
{
    /// <summary>Verified dimensions must match the built family within 2 mm on every axis.</summary>
    public const double ConfirmedToleranceM = 0.002;

    public static string Symbol(LengthUnit unit) => unit == LengthUnit.Inches ? "in" : "mm";

    public static void Validate(Vec3 size)
    {
        if (!size.IsFinite || size.X <= 0 || size.Y <= 0 || size.Z <= 0 ||
            size.X > 2 * StudioLimits.RoomHalfWidthM || size.Y > 2 * StudioLimits.RoomHalfDepthM || size.Z > StudioLimits.RoomHeightM)
            throw new ArgumentException("Use positive dimensions up to 8 m wide, 6 m deep and 4 m high.");
    }

    /// <summary>Returns null when all three fields are blank, meaning "estimate the size for me".</summary>
    public static Vec3? Parse(string width, string depth, string height, LengthUnit unit)
    {
        var fields = new[] { width, depth, height };
        if (fields.All(string.IsNullOrWhiteSpace)) return null;
        if (fields.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Enter width, depth and height, or leave all three blank to estimate.");
        var size = new Vec3(ParseLength(width, unit), ParseLength(depth, unit), ParseLength(height, unit));
        Validate(size);
        return size;
    }

    /// <summary>Parses one length and returns metres.</summary>
    public static double ParseLength(string text, LengthUnit unit)
    {
        var value = text.Trim().Replace('’', '\'').Replace('”', '"').Replace('′', '\'').Replace('″', '"');
        if (value.Length == 0) throw new ArgumentException("Enter a length.");

        var feet = FeetAndInches().Match(value);
        if (feet.Success && (feet.Groups["ft"].Success || value.Contains('"')))
        {
            var total = 0.0;
            if (feet.Groups["ft"].Success) total += Number(feet.Groups["ft"].Value) * 12;
            if (feet.Groups["in"].Success) total += Number(feet.Groups["in"].Value);
            if (feet.Groups["num"].Success) total += Number(feet.Groups["num"].Value) / Number(feet.Groups["den"].Value);
            return total * 0.0254;
        }

        var suffixed = Suffixed().Match(value);
        if (!suffixed.Success) throw new ArgumentException($"\"{text.Trim()}\" is not a length. Try 650, 25.5 or 2'-6\".");
        var number = Number(suffixed.Groups["n"].Value);
        return suffixed.Groups["u"].Value.ToLowerInvariant() switch
        {
            "mm" => number / 1000,
            "cm" => number / 100,
            "m" => number,
            "in" or "\"" => number * 0.0254,
            "ft" or "'" => number * 0.3048,
            _ => unit == LengthUnit.Inches ? number * 0.0254 : number / 1000
        };
    }

    public static string Format(double metres, LengthUnit unit) =>
        (metres / (unit == LengthUnit.Inches ? 0.0254 : 0.001)).ToString(unit == LengthUnit.Inches ? "0.##" : "0.#", CultureInfo.CurrentCulture);

    private static double Number(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return value;
        throw new ArgumentException($"\"{text}\" is not a number.");
    }

    // 2'-6", 2' 6", 2'6 1/2", 6", 3'
    [GeneratedRegex("""^\s*(?:(?<ft>\d+(?:[.,]\d+)?)\s*'\s*-?\s*)?(?:(?<in>\d+(?:[.,]\d+)?)?\s*(?:(?<num>\d+)\s*/\s*(?<den>[1-9]\d*))?\s*"?)?\s*$""")]
    private static partial Regex FeetAndInches();

    [GeneratedRegex("""^\s*(?<n>\d+(?:[.,]\d+)?|[.,]\d+)\s*(?<u>mm|cm|m|in|ft|"|')?\s*$""", RegexOptions.IgnoreCase)]
    private static partial Regex Suffixed();
}
