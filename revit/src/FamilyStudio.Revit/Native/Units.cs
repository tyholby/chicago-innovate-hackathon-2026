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

    /// <summary>A unit direction, which needs no unit conversion.</summary>
    public static XYZ Direction(Vec3 v) => new XYZ(v.X, v.Y, v.Z).Normalize();

    /// <summary>
    /// Bounds of solids in metres, measured from the geometry itself: every face's finest mesh, every
    /// edge's points, and the exact extremes of circular edges. Tight to well under a millimetre for
    /// the shapes Family Studio builds, unlike element bounding boxes, which may be loose around curves.
    /// </summary>
    public static Box3 MeasureSolids(IEnumerable<Solid> solids)
    {
        var points = new List<XYZ>();
        foreach (var solid in solids)
        {
            foreach (Face face in solid.Faces) points.AddRange(face.Triangulate(1.0).Vertices);
            foreach (Edge edge in solid.Edges)
            {
                var curve = edge.AsCurve();
                points.AddRange(curve.Tessellate());
                if (curve is Arc arc) points.AddRange(ArcExtremes(arc));
            }
        }
        if (points.Count == 0) throw new InvalidOperationException("Revit returned no geometry to measure.");
        return Box3.Of(points.Select(Vec));
    }

    /// <summary>Where a circular edge reaches furthest along each axis, if that point lies on the edge.</summary>
    private static IEnumerable<XYZ> ArcExtremes(Arc arc)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            var peak = Math.Atan2(arc.YDirection[axis], arc.XDirection[axis]);
            foreach (var angle in new[] { peak, peak + Math.PI })
            {
                var point = arc.Center + arc.XDirection * (arc.Radius * Math.Cos(angle)) + arc.YDirection * (arc.Radius * Math.Sin(angle));
                if (!arc.IsBound || arc.Project(point).Distance < 1e-9) yield return point;
            }
        }
    }

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
