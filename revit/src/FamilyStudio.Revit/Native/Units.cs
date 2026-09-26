using Autodesk.Revit.DB;
using FamilyStudio.Core.Model;

namespace FamilyStudio.Revit.Native;

/// <summary>Conversions between the pipeline's metres and Revit's internal feet.</summary>
internal static class Units
{
    public static double Feet(double metres) => UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters);

    public static double Metres(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);

    public static XYZ Point(Vec3 p) => new(Feet(p.X), Feet(p.Y), Feet(p.Z));

    public static XYZ Point(double x, double y, double z) => new(Feet(x), Feet(y), Feet(z));

    public static Vec3 Vec(XYZ p) => new(Metres(p.X), Metres(p.Y), Metres(p.Z));

    /// <summary>Axis-aligned bounds of elements in metres, from every corner of each (possibly rotated) box.</summary>
    public static Box3 Measure(IEnumerable<Element> elements)
    {
        var points = new List<Vec3>();
        foreach (var element in elements)
        {
            var box = element.get_BoundingBox(null);
            if (box is null) continue;
            foreach (var x in new[] { box.Min.X, box.Max.X })
            foreach (var y in new[] { box.Min.Y, box.Max.Y })
            foreach (var z in new[] { box.Min.Z, box.Max.Z })
                points.Add(Vec(box.Transform.OfPoint(new XYZ(x, y, z))));
        }
        if (points.Count == 0) throw new InvalidOperationException("Revit returned no geometry to measure.");
        return Box3.Of(points);
    }
}
