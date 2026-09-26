using System.Globalization;
using System.Text.Json.Serialization;

namespace FamilyStudio.Core.Model;

/// <summary>
/// A point or a size in metres. X is width, Y is depth and Z is height. Family fronts face
/// local negative Y, and a family's origin sits on its base at Z = 0.
/// </summary>
public sealed record Vec3(double X, double Y, double Z)
{
    public static Vec3 Zero { get; } = new(0, 0, 0);

    public double this[int axis] => axis switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        _ => throw new ArgumentOutOfRangeException(nameof(axis))
    };

    [JsonIgnore]
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>Formats a size as "W x D x H mm", the way architects read furniture dimensions.</summary>
    public string ToMillimetres() => string.Format(CultureInfo.InvariantCulture,
        "{0:0} x {1:0} x {2:0} mm", X * 1000, Y * 1000, Z * 1000);

    public override string ToString() => string.Format(CultureInfo.InvariantCulture,
        "({0:0.###}, {1:0.###}, {2:0.###}) m", X, Y, Z);
}
