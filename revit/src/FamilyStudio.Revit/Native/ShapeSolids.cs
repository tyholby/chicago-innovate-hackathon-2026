using Autodesk.Revit.DB;
using FamilyStudio.Core.Model;

namespace FamilyStudio.Revit.Native;

/// <summary>
/// Native solids for each part shape, built only from extrusions and revolutions, the most robust
/// geometry Revit creates. Each construction is exactly the geometry the core validates: a rounded
/// box is three rounded slabs plus corner spheres, a tube is cylinders plus spheres at its points.
/// Pieces are united into one solid where Revit's union succeeds; otherwise they stay separate,
/// overlapping forms that look the same. Anything Revit refuses falls back to a simpler solid with
/// the same extent, and the fallback is reported.
/// </summary>
internal static class ShapeSolids
{
    /// <summary>Below this radius a box is built with sharp edges.</summary>
    private const double RoundingM = 0.0005;

    /// <summary>Revit refuses curves shorter than its short-curve tolerance (about 0.8 mm).</summary>
    private const double ShortestCurveM = 0.001;

    public static IReadOnlyList<Solid> Build(RecipePart part, ICollection<string> notes)
    {
        switch (part.Shape)
        {
            case PartShapes.Box when part.RadiusM < RoundingM:
                return new[] { Tilted(SharpBox(part.MinM!, part.MaxM!), part) };
            case PartShapes.Box:
                try
                {
                    return Unite(RoundedBox(part), part.Name, notes).Select(solid => Tilted(solid, part)).ToArray();
                }
                catch (Exception ex) when (IsGeometryFailure(ex))
                {
                    notes.Add($"{part.Name}: rounded edges were refused ({ex.Message}); built with sharp edges.");
                    return new[] { Tilted(SharpBox(part.MinM!, part.MaxM!), part) };
                }
            case PartShapes.Cylinder:
                return new[] { Frustum(part.PointsM![0], part.PointsM[1], part.RadiusM, Shapes.EndRadius(part)) };
            case PartShapes.Sphere:
                return new[] { Ball(part.PointsM![0], part.RadiusM) };
            case PartShapes.Tube:
                return Unite(TubePieces(part), part.Name, notes);
            case PartShapes.Profile:
                try
                {
                    return new[] { Prism(part.Plane!, Shapes.Outline(part.OutlineM!, part.RadiusM), part.FromM!.Value, part.ToM!.Value) };
                }
                catch (Exception ex) when (IsGeometryFailure(ex) && part.RadiusM > 0)
                {
                    notes.Add($"{part.Name}: rounded corners were refused ({ex.Message}); built with sharp corners.");
                    return new[] { Prism(part.Plane!, Shapes.Outline(part.OutlineM!, 0), part.FromM!.Value, part.ToM!.Value) };
                }
            default:
                throw new InvalidOperationException($"Unknown shape \"{part.Shape}\".");
        }
    }

    // ---- shapes ----------------------------------------------------------------------------

    private static Solid SharpBox(Vec3 min, Vec3 max)
    {
        var rectangle = new[] { new[] { min.X, min.Y }, new[] { max.X, min.Y }, new[] { max.X, max.Y }, new[] { min.X, max.Y } };
        return Prism(ProfilePlanes.Plan, Shapes.Outline(rectangle, 0), min.Z, max.Z);
    }

    /// <summary>
    /// A box with all edges rounded: the core box grown by the radius. Three rounded slabs, one along
    /// each axis, form its faces and edges, and a sphere at each core corner rounds the corners.
    /// </summary>
    private static List<Solid> RoundedBox(RecipePart part)
    {
        var (min, max, r) = (part.MinM!, part.MaxM!, part.RadiusM);
        var pieces = new List<Solid>();
        void Slab(string plane, double u0, double v0, double u1, double v1, double from, double to)
        {
            if (to - from < 1e-6) return; // a side exactly twice the radius: the other pieces cover it
            var rectangle = new[] { new[] { u0, v0 }, new[] { u1, v0 }, new[] { u1, v1 }, new[] { u0, v1 } };
            pieces.Add(Prism(plane, Shapes.Outline(rectangle, r), from, to));
        }
        Slab(ProfilePlanes.Plan, min.X, min.Y, max.X, max.Y, min.Z + r, max.Z - r);
        Slab(ProfilePlanes.Side, min.Y, min.Z, max.Y, max.Z, min.X + r, max.X - r);
        Slab(ProfilePlanes.Front, min.X, min.Z, max.X, max.Z, min.Y + r, max.Y - r);

        var corners = new List<Vec3>();
        foreach (var x in new[] { min.X + r, max.X - r })
        foreach (var y in new[] { min.Y + r, max.Y - r })
        foreach (var z in new[] { min.Z + r, max.Z - r })
            if (!corners.Any(c => Math.Abs(c.X - x) + Math.Abs(c.Y - y) + Math.Abs(c.Z - z) < 1e-9))
                corners.Add(new Vec3(x, y, z));
        pieces.AddRange(corners.Select(c => Ball(c, r)));
        return pieces;
    }

    private static List<Solid> TubePieces(RecipePart part)
    {
        var points = part.PointsM!;
        var pieces = new List<Solid>();
        for (var i = 0; i + 1 < points.Length; i++) pieces.Add(Frustum(points[i], points[i + 1], part.RadiusM, part.RadiusM));
        pieces.AddRange(points.Select(p => Ball(p, part.RadiusM)));
        return pieces;
    }

    /// <summary>A cylinder or taper: a trapezoid beside the axis, revolved once around it.</summary>
    private static Solid Frustum(Vec3 from, Vec3 to, double startRadiusM, double endRadiusM)
    {
        var start = Units.Point(from);
        var end = Units.Point(to);
        var axis = (end - start).Normalize();
        var x = Perpendicular(axis);
        var frame = new Frame(start, x, axis.CrossProduct(x), axis); // right-handed: y = z cross x
        var a = start + x * Units.Feet(startRadiusM);
        var b = end + x * Units.Feet(endRadiusM);
        var profile = CurveLoop.Create(new List<Curve>
        {
            Line.CreateBound(start, a),
            Line.CreateBound(a, b),
            Line.CreateBound(b, end),
            Line.CreateBound(end, start)
        });
        return GeometryCreationUtilities.CreateRevolvedGeometry(frame, new List<CurveLoop> { profile }, 0, 2 * Math.PI);
    }

    /// <summary>A sphere: a half circle closed along the axis, revolved once around it.</summary>
    private static Solid Ball(Vec3 centreM, double radiusM)
    {
        var centre = Units.Point(centreM);
        var arc = Arc.Create(centre, Units.Feet(radiusM), -Math.PI / 2, Math.PI / 2, XYZ.BasisX, XYZ.BasisZ);
        var profile = new CurveLoop();
        profile.Append(arc);
        profile.Append(Line.CreateBound(arc.GetEndPoint(1), arc.GetEndPoint(0)));
        return GeometryCreationUtilities.CreateRevolvedGeometry(new Frame(centre, XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ),
            new List<CurveLoop> { profile }, 0, 2 * Math.PI);
    }

    /// <summary>An outline of lines and arcs in a profile plane, extruded from fromM to toM along the plane's normal.</summary>
    private static Solid Prism(string plane, IReadOnlyList<OutlineSegment> outline, double fromM, double toM)
    {
        XYZ Map(P2 p) => Units.Point(Shapes.ProfilePoint(plane, p, fromM));
        var pieces = outline.Select(s => (Start: Map(s.Start), End: Map(s.End), Mid: s.IsArc ? Map(s.At(0.5)) : null)).ToList();

        // A curve too short for Revit (a sliver of edge left between two fillets, or a tiny fillet)
        // is dropped, and its neighbours meet at its midpoint instead: a sub-millimetre change.
        var shortest = Units.Feet(ShortestCurveM);
        for (var i = 0; i < pieces.Count && pieces.Count > 3;)
        {
            var piece = pieces[i];
            var length = piece.Start.DistanceTo(piece.End);
            if (length >= shortest || (piece.Mid is not null && piece.Start.DistanceTo(piece.Mid) + piece.Mid.DistanceTo(piece.End) >= shortest))
            {
                i++;
                continue;
            }
            var joint = (piece.Start + piece.End) / 2;
            var before = (i + pieces.Count - 1) % pieces.Count;
            var after = (i + 1) % pieces.Count;
            pieces[before] = pieces[before] with { End = joint };
            pieces[after] = pieces[after] with { Start = joint };
            pieces.RemoveAt(i);
        }

        var loop = new CurveLoop();
        foreach (var piece in pieces)
            loop.Append(piece.Mid is { } mid ? Arc.Create(piece.Start, piece.End, mid) : Line.CreateBound(piece.Start, piece.End));
        return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop },
            Units.Direction(Shapes.ProfileNormal(plane)), Units.Feet(toM - fromM));
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>Joins overlapping pieces into one solid, or keeps them separate if Revit cannot.</summary>
    private static IReadOnlyList<Solid> Unite(IReadOnlyList<Solid> pieces, string name, ICollection<string> notes)
    {
        if (pieces.Count < 2) return pieces;
        try
        {
            var united = pieces[0];
            for (var i = 1; i < pieces.Count; i++)
                united = BooleanOperationsUtils.ExecuteBooleanOperation(united, pieces[i], BooleanOperationsType.Union);
            if (united is { Volume: > 0 }) return new[] { united };
        }
        catch (Exception ex) when (IsGeometryFailure(ex)) { }
        notes.Add($"{name}: kept as {pieces.Count} overlapping solids, because Revit could not unite them.");
        return pieces;
    }

    /// <summary>A box part's tilt about its own X axis through its centre, as the validator measures it.</summary>
    private static Solid Tilted(Solid solid, RecipePart part)
    {
        if (Math.Abs(part.TiltDegrees) < 1e-9) return solid;
        var centre = (Units.Point(part.MinM!) + Units.Point(part.MaxM!)) / 2;
        return SolidUtils.CreateTransformed(solid, Transform.CreateRotationAtPoint(XYZ.BasisX, part.TiltDegrees * Math.PI / 180, centre));
    }

    private static XYZ Perpendicular(XYZ axis) =>
        axis.CrossProduct(Math.Abs(axis.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX).Normalize();

    private static bool IsGeometryFailure(Exception ex) =>
        ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException;
}
