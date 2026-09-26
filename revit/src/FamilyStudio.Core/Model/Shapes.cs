namespace FamilyStudio.Core.Model;

/// <summary>A point in a profile's own plane: [u, v] in metres.</summary>
public readonly record struct P2(double U, double V)
{
    public static P2 operator +(P2 a, P2 b) => new(a.U + b.U, a.V + b.V);
    public static P2 operator -(P2 a, P2 b) => new(a.U - b.U, a.V - b.V);
    public static P2 operator *(P2 a, double k) => new(a.U * k, a.V * k);
    public double Length => Math.Sqrt(U * U + V * V);
    public P2 Unit => this * (1 / Length);
    public static double Dot(P2 a, P2 b) => a.U * b.U + a.V * b.V;
    public static double Cross(P2 a, P2 b) => a.U * b.V - a.V * b.U;
}

/// <summary>
/// One piece of a profile outline: a line from <see cref="Start"/> to <see cref="End"/>, or, when
/// <see cref="Radius"/> is positive, a circular arc around <see cref="Centre"/> that starts at
/// <see cref="StartAngle"/> and turns by <see cref="Sweep"/> radians (counterclockwise when positive).
/// </summary>
public sealed record OutlineSegment(P2 Start, P2 End, P2 Centre, double Radius, double StartAngle, double Sweep)
{
    public bool IsArc => Radius > 0;

    public P2 At(double t) => IsArc
        ? new P2(Centre.U + Radius * Math.Cos(StartAngle + t * Sweep), Centre.V + Radius * Math.Sin(StartAngle + t * Sweep))
        : Start + (End - Start) * t;

    public static OutlineSegment Line(P2 start, P2 end) => new(start, end, default, 0, 0, 0);
}

/// <summary>A horizontal rectangle another item can stand on, in family coordinates (MinM.Z == MaxM.Z == its height).</summary>
public sealed record SupportSurface(string Name, Vec3 MinM, Vec3 MaxM);

/// <summary>A flat polygon of a part's surface, for drawings. Crisp faces are drawn with outlines.</summary>
public sealed record MeshFace(Vec3[] Points, Vec3 Normal, bool Crisp);

/// <summary>
/// The exact geometry of every part shape: mirror copies, bounding boxes, rounded outlines, contact
/// points, support surfaces and a drawing mesh. Validation, placement, the probe's drawings and the
/// native builder all use these, so what is checked is what gets built.
/// </summary>
public static class Shapes
{
    /// <summary>The smallest radius, thickness or length any part may have.</summary>
    public const double MinSizeM = 0.002;

    /// <summary>The parts to build: each part, then its mirror image when <see cref="RecipePart.Mirror"/> is set.</summary>
    public static IEnumerable<RecipePart> Expand(IEnumerable<RecipePart> parts)
    {
        foreach (var part in parts)
        {
            yield return part.Mirror ? part with { Mirror = false } : part;
            if (part.Mirror) yield return Mirrored(part);
        }
    }

    /// <summary>The part reflected across X = 0.</summary>
    public static RecipePart Mirrored(RecipePart p)
    {
        var sideProfile = p.Shape == PartShapes.Profile && p.Plane == ProfilePlanes.Side;
        var flipsOutline = p.Shape == PartShapes.Profile && !sideProfile;
        return p with
        {
            Name = p.Name + " mirrored",
            Mirror = false,
            MinM = p.MinM is not null && p.MaxM is not null ? p.MinM with { X = -p.MaxM.X } : p.MinM,
            MaxM = p.MinM is not null && p.MaxM is not null ? p.MaxM with { X = -p.MinM.X } : p.MaxM,
            PointsM = p.PointsM?.Select(q => q with { X = -q.X }).ToArray(),
            OutlineM = flipsOutline ? p.OutlineM?.Select(o => o is { Length: 2 } ? new[] { -o[0], o[1] } : o).ToArray() : p.OutlineM,
            FromM = sideProfile ? -p.ToM : p.FromM,
            ToM = sideProfile ? -p.FromM : p.ToM
        };
    }

    public static double EndRadius(RecipePart part) => part.EndRadiusM ?? part.RadiusM;

    /// <summary>
    /// Stands a round floor support exactly on the floor. A splayed leg's end disc tilts with it and
    /// dips a few millimetres below Z = 0; a ball foot or a sled tube may be drawn through its centre.
    /// Such parts (cylinders, spheres and tubes marked as floor supports) are moved up or down, never
    /// by more than their radius, so their lowest point is exactly at Z = 0. Assumes a valid part.
    /// </summary>
    public static RecipePart Settle(RecipePart part)
    {
        if (!part.IsFloorSupport || part.PointsM is null ||
            part.Shape is not (PartShapes.Cylinder or PartShapes.Sphere or PartShapes.Tube)) return part;
        var lowest = Bounds(part).Min.Z;
        var reach = part.Shape == PartShapes.Cylinder ? Math.Max(part.RadiusM, EndRadius(part)) : part.RadiusM;
        if (Math.Abs(lowest) < 1e-12 || Math.Abs(lowest) > reach + 1e-12) return part;
        return part with { PointsM = part.PointsM.Select(p => p with { Z = p.Z - lowest }).ToArray() };
    }

    // ---- bounds ----------------------------------------------------------------------------

    /// <summary>
    /// The exact axis-aligned bounds of one (already valid) part, optionally turned about Z by
    /// <paramref name="rotationDegrees"/> and moved by <paramref name="offset"/>, as a placement does.
    /// </summary>
    public static Box3 Bounds(RecipePart part, double rotationDegrees = 0, Vec3? offset = null)
    {
        var pose = new Pose(rotationDegrees, offset ?? Vec3.Zero);
        switch (part.Shape)
        {
            case PartShapes.Box:
                // A rounded box is its inner (core) box grown by the radius in every direction.
                var corners = BoxCorners(part.MinM!, part.MaxM!, part.TiltDegrees, part.RadiusM).Select(pose.Apply);
                return part.RadiusM > 0 ? Box3.Of(corners).Grow(part.RadiusM) : Box3.Of(corners);
            case PartShapes.Cylinder:
                var a = pose.Apply(part.PointsM![0]);
                var b = pose.Apply(part.PointsM[1]);
                var axis = V.Unit(b - a);
                return Box3.Of(Circle(a, axis, part.RadiusM).Extremes().Concat(Circle(b, axis, EndRadius(part)).Extremes()));
            case PartShapes.Sphere:
                return Box3.Of(new[] { pose.Apply(part.PointsM![0]) }).Grow(part.RadiusM);
            case PartShapes.Tube:
                // A tube is the path grown by its radius: capsules between spheres.
                return Box3.Of(part.PointsM!.Select(pose.Apply)).Grow(part.RadiusM);
            case PartShapes.Profile:
                return Box3.Of(ProfileArcs(part, pose).SelectMany(arc => arc.Extremes()));
            default:
                throw new ArgumentException($"Unknown shape \"{part.Shape}\".");
        }
    }

    /// <summary>The eight corners of a box shrunk by <paramref name="inset"/> on every side, tilted about X through its centre.</summary>
    public static IEnumerable<Vec3> BoxCorners(Vec3 min, Vec3 max, double tiltDegrees, double inset = 0)
    {
        var centreY = (min.Y + max.Y) / 2;
        var centreZ = (min.Z + max.Z) / 2;
        var radians = tiltDegrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        foreach (var x in new[] { min.X + inset, max.X - inset })
        foreach (var y in new[] { min.Y + inset, max.Y - inset })
        foreach (var z in new[] { min.Z + inset, max.Z - inset })
        {
            var dy = y - centreY;
            var dz = z - centreZ;
            yield return new Vec3(x, centreY + dy * cos - dz * sin, centreZ + dy * sin + dz * cos);
        }
    }

    // ---- profiles --------------------------------------------------------------------------

    /// <summary>
    /// A profile's outline as lines and fillet arcs, counterclockwise in its plane. Each corner is
    /// rounded by <paramref name="radius"/>, reduced where the neighbouring edges are too short, so
    /// fillets never overlap. Assumes a valid outline (see <see cref="Validation.RecipeRules"/>).
    /// </summary>
    public static IReadOnlyList<OutlineSegment> Outline(double[][] outline, double radius)
    {
        var points = Clean(outline.Select(o => new P2(o[0], o[1])).ToList());
        if (SignedArea(points) < 0) points.Reverse();
        var n = points.Count;
        var corners = new (P2 In, P2 Out, OutlineSegment? Arc)[n];
        for (var i = 0; i < n; i++)
        {
            var a = points[(i + n - 1) % n];
            var b = points[i];
            var c = points[(i + 1) % n];
            corners[i] = (b, b, null);
            if (radius <= 0) continue;
            var d1 = (a - b).Unit;
            var d2 = (c - b).Unit;
            var angle = Math.Acos(Math.Clamp(P2.Dot(d1, d2), -1, 1));
            if (angle < 1e-6 || angle > Math.PI - 1e-6) continue; // a spike or a straight run: nothing to round
            var reach = Math.Min(radius / Math.Tan(angle / 2), 0.5 * Math.Min((a - b).Length, (c - b).Length));
            var r = reach * Math.Tan(angle / 2);
            var t1 = b + d1 * reach;
            var t2 = b + d2 * reach;
            var centre = b + (d1 + d2).Unit * (r / Math.Sin(angle / 2));
            var left = P2.Cross(b - a, c - b) > 0; // convex corner of a counterclockwise outline
            var sweep = (left ? 1 : -1) * (Math.PI - angle);
            corners[i] = (t1, t2, new OutlineSegment(t1, t2, centre, r, Math.Atan2(t1.V - centre.V, t1.U - centre.U), sweep));
        }

        var segments = new List<OutlineSegment>();
        for (var i = 0; i < n; i++)
        {
            var from = corners[(i + n - 1) % n].Out;
            var to = corners[i].In;
            if ((to - from).Length > 1e-9) segments.Add(OutlineSegment.Line(from, to));
            if (corners[i].Arc is { } arc) segments.Add(arc);
        }
        return segments;
    }

    /// <summary>Signed area of a polygon: positive when counterclockwise.</summary>
    public static double SignedArea(IReadOnlyList<P2> points)
    {
        var sum = 0.0;
        for (var i = 0; i < points.Count; i++)
            sum += P2.Cross(points[i], points[(i + 1) % points.Count]);
        return sum / 2;
    }

    /// <summary>Drops repeated points, including a closing point equal to the first.</summary>
    public static List<P2> Clean(List<P2> points)
    {
        var result = new List<P2>();
        foreach (var p in points)
            if (result.Count == 0 || (p - result[^1]).Length > 1e-7) result.Add(p);
        while (result.Count > 1 && (result[0] - result[^1]).Length <= 1e-7) result.RemoveAt(result.Count - 1);
        return result;
    }

    /// <summary>Maps a profile point [u, v] at depth w along the extrusion axis into family coordinates.</summary>
    public static Vec3 ProfilePoint(string plane, P2 p, double w) => plane switch
    {
        ProfilePlanes.Front => new Vec3(p.U, w, p.V),
        ProfilePlanes.Side => new Vec3(w, p.U, p.V),
        _ => new Vec3(p.U, p.V, w)
    };

    /// <summary>The unit extrusion direction of a profile plane (from FromM toward ToM).</summary>
    public static Vec3 ProfileNormal(string plane) => plane switch
    {
        ProfilePlanes.Front => new Vec3(0, 1, 0),
        ProfilePlanes.Side => new Vec3(1, 0, 0),
        _ => new Vec3(0, 0, 1)
    };

    // ---- contact and support --------------------------------------------------------------

    /// <summary>
    /// Points on the part's surface that include its lowest points, so a caller can find where it
    /// meets a floor or a support: corners, the bottoms of balls and bars, samples of every circle.
    /// </summary>
    public static IEnumerable<Vec3> ContactPoints(RecipePart part, double rotationDegrees = 0, Vec3? offset = null)
    {
        var pose = new Pose(rotationDegrees, offset ?? Vec3.Zero);
        var down = new Vec3(0, 0, -part.RadiusM);
        switch (part.Shape)
        {
            case PartShapes.Box:
                return BoxCorners(part.MinM!, part.MaxM!, part.TiltDegrees, part.RadiusM).Select(c => pose.Apply(c) + down);
            case PartShapes.Cylinder:
                var a = pose.Apply(part.PointsM![0]);
                var b = pose.Apply(part.PointsM[1]);
                var axis = V.Unit(b - a);
                return Circle(a, axis, part.RadiusM).Samples(24).Concat(Circle(a, axis, part.RadiusM).Extremes())
                    .Concat(Circle(b, axis, EndRadius(part)).Samples(24)).Concat(Circle(b, axis, EndRadius(part)).Extremes());
            case PartShapes.Sphere:
                return new[] { pose.Apply(part.PointsM![0]) + down };
            case PartShapes.Tube:
                return part.PointsM!.Select(p => pose.Apply(p) + down);
            case PartShapes.Profile:
                return ProfileArcs(part, pose).SelectMany(arc => arc.Samples(8).Concat(arc.Extremes()));
            default:
                return Array.Empty<Vec3>();
        }
    }

    /// <summary>
    /// The flat tops another item can stand on: level boxes (inside any edge rounding) and the
    /// largest square inside the top of an upright cylinder.
    /// </summary>
    public static IEnumerable<SupportSurface> Surfaces(RecipePart part)
    {
        if (part.Shape == PartShapes.Box && Math.Abs(part.TiltDegrees) < 1e-9)
        {
            var r = part.RadiusM;
            var min = new Vec3(part.MinM!.X + r, part.MinM.Y + r, part.MaxM!.Z);
            var max = new Vec3(part.MaxM.X - r, part.MaxM.Y - r, part.MaxM.Z);
            if (max.X - min.X > 1e-6 && max.Y - min.Y > 1e-6) yield return new SupportSurface(part.Name, min, max);
        }
        else if (part.Shape == PartShapes.Cylinder)
        {
            var a = part.PointsM![0];
            var b = part.PointsM[1];
            var axis = V.Unit(b - a);
            if (Math.Abs(axis.Z) < 1 - 1e-6) yield break; // only upright cylinders have a level top
            var (top, radius) = a.Z > b.Z ? (a, part.RadiusM) : (b, EndRadius(part));
            var half = radius / Math.Sqrt(2);
            if (half > 1e-6) yield return new SupportSurface(part.Name, new Vec3(top.X - half, top.Y - half, top.Z), new Vec3(top.X + half, top.Y + half, top.Z));
        }
    }

    // ---- drawing mesh -----------------------------------------------------------------------

    /// <summary>
    /// A polygon mesh of the part's surface, placed like <see cref="Bounds"/>, with outward normals.
    /// Detailed enough to judge a silhouette in a drawing; not used for any measurement.
    /// </summary>
    public static IEnumerable<MeshFace> Mesh(RecipePart part, double rotationDegrees = 0, Vec3? offset = null)
    {
        var pose = new Pose(rotationDegrees, offset ?? Vec3.Zero);
        var faces = part.Shape switch
        {
            PartShapes.Box when part.RadiusM <= 0 => SharpBoxMesh(part),
            PartShapes.Box => RoundedBoxMesh(part),
            PartShapes.Cylinder => FrustumMesh(part.PointsM![0], part.PointsM[1], part.RadiusM, EndRadius(part), 24, caps: true),
            PartShapes.Sphere => SphereMesh(part.PointsM![0], part.RadiusM),
            PartShapes.Tube => TubeMesh(part),
            PartShapes.Profile => ProfileMesh(part),
            _ => Enumerable.Empty<MeshFace>()
        };
        foreach (var face in faces)
            yield return face with { Points = face.Points.Select(pose.Apply).ToArray(), Normal = pose.Turn(face.Normal) };
    }

    private static IEnumerable<MeshFace> SharpBoxMesh(RecipePart part)
    {
        var c = BoxCorners(part.MinM!, part.MaxM!, part.TiltDegrees).ToArray();
        int I(int x, int y, int z) => x * 4 + y * 2 + z;
        var quads = new[]
        {
            new[] { I(0,0,1), I(1,0,1), I(1,1,1), I(0,1,1) }, new[] { I(0,0,0), I(0,1,0), I(1,1,0), I(1,0,0) },
            new[] { I(0,0,0), I(1,0,0), I(1,0,1), I(0,0,1) }, new[] { I(1,1,0), I(0,1,0), I(0,1,1), I(1,1,1) },
            new[] { I(1,0,0), I(1,1,0), I(1,1,1), I(1,0,1) }, new[] { I(0,1,0), I(0,0,0), I(0,0,1), I(0,1,1) }
        };
        var centre = V.Mean(c);
        return quads.Select(q => Face(q.Select(i => c[i]).ToArray(), centre, crisp: true));
    }

    private static IEnumerable<MeshFace> RoundedBoxMesh(RecipePart part)
    {
        // Sample the box's six faces, then pull every sample onto the rounded surface: the nearest
        // point of the core box plus the radius toward the sample.
        var min = part.MinM!;
        var max = part.MaxM!;
        var r = part.RadiusM;
        var centre = new Vec3((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        var half = new Vec3((max.X - min.X) / 2, (max.Y - min.Y) / 2, (max.Z - min.Z) / 2);
        var core = new Vec3(half.X - r, half.Y - r, half.Z - r);
        double[] Samples(double h, double c) => new[] { -h, -c - (h - c) / 2, -c, c, c + (h - c) / 2, h }
            .Distinct().OrderBy(v => v).ToArray();
        var sx = Samples(half.X, core.X);
        var sy = Samples(half.Y, core.Y);
        var sz = Samples(half.Z, core.Z);
        Vec3 Round(Vec3 q)
        {
            var nearest = new Vec3(Math.Clamp(q.X, -core.X, core.X), Math.Clamp(q.Y, -core.Y, core.Y), Math.Clamp(q.Z, -core.Z, core.Z));
            var outward = q - nearest;
            var length = V.Length(outward);
            return length < 1e-12 ? q : nearest + V.Scale(outward, r / length);
        }
        var tilt = new Tilt(part.TiltDegrees, centre);
        var faces = new List<MeshFace>();
        void Grid(double[] us, double[] vs, Func<double, double, Vec3> at)
        {
            for (var i = 0; i + 1 < us.Length; i++)
            for (var j = 0; j + 1 < vs.Length; j++)
            {
                var quad = new[] { at(us[i], vs[j]), at(us[i + 1], vs[j]), at(us[i + 1], vs[j + 1]), at(us[i], vs[j + 1]) }
                    .Select(q => tilt.Apply(Round(q) + centre)).ToArray();
                faces.Add(Face(quad, tilt.Apply(centre), crisp: false));
            }
        }
        Grid(sx, sy, (u, v) => new Vec3(u, v, half.Z));
        Grid(sx, sy, (u, v) => new Vec3(u, v, -half.Z));
        Grid(sx, sz, (u, v) => new Vec3(u, -half.Y, v));
        Grid(sx, sz, (u, v) => new Vec3(u, half.Y, v));
        Grid(sy, sz, (u, v) => new Vec3(half.X, u, v));
        Grid(sy, sz, (u, v) => new Vec3(-half.X, u, v));
        return faces;
    }

    private static IEnumerable<MeshFace> FrustumMesh(Vec3 a, Vec3 b, double ra, double rb, int segments, bool caps)
    {
        var axis = V.Unit(b - a);
        var bottom = Circle(a, axis, ra).Samples(segments).ToArray();
        var top = Circle(b, axis, rb).Samples(segments).ToArray();
        var centre = V.Scale(a + b, 0.5);
        for (var i = 0; i < segments; i++)
        {
            var j = (i + 1) % segments;
            yield return Face(new[] { bottom[i], bottom[j], top[j], top[i] }, centre, crisp: false);
        }
        if (!caps) yield break;
        yield return Face(bottom, centre, crisp: true);
        yield return Face(top, centre, crisp: true);
    }

    private static IEnumerable<MeshFace> SphereMesh(Vec3 c, double r)
    {
        const int rings = 8, segments = 16;
        Vec3 At(int ring, int segment)
        {
            var polar = Math.PI * ring / rings;
            var azimuth = 2 * Math.PI * segment / segments;
            return new Vec3(c.X + r * Math.Sin(polar) * Math.Cos(azimuth), c.Y + r * Math.Sin(polar) * Math.Sin(azimuth), c.Z + r * Math.Cos(polar));
        }
        for (var i = 0; i < rings; i++)
        for (var j = 0; j < segments; j++)
            yield return Face(new[] { At(i, j), At(i + 1, j), At(i + 1, j + 1), At(i, j + 1) }.Distinct().ToArray(), c, crisp: false);
    }

    private static IEnumerable<MeshFace> TubeMesh(RecipePart part)
    {
        var points = part.PointsM!;
        for (var i = 0; i + 1 < points.Length; i++)
            foreach (var face in FrustumMesh(points[i], points[i + 1], part.RadiusM, part.RadiusM, 16, caps: false))
                yield return face;
        foreach (var point in points)
        foreach (var face in SphereMesh(point, part.RadiusM))
            yield return face;
    }

    private static IEnumerable<MeshFace> ProfileMesh(RecipePart part)
    {
        var plane = part.Plane!;
        var outline = OutlinePoints(Outline(part.OutlineM!, part.RadiusM), 12).ToArray();
        var near = outline.Select(p => ProfilePoint(plane, p, part.FromM!.Value)).ToArray();
        var far = outline.Select(p => ProfilePoint(plane, p, part.ToM!.Value)).ToArray();
        var normal = ProfileNormal(plane);
        yield return new MeshFace(near.Reverse().ToArray(), V.Scale(normal, -1), true);
        yield return new MeshFace(far, normal, true);
        for (var i = 0; i < outline.Length; i++)
        {
            var j = (i + 1) % outline.Length;
            // The outline runs counterclockwise, so the outside of edge i is on its right.
            var edge = outline[j] - outline[i];
            var outward = ProfilePoint(plane, new P2(edge.V, -edge.U), 0) - ProfilePoint(plane, new P2(0, 0), 0);
            yield return new MeshFace(new[] { near[i], near[j], far[j], far[i] }, V.Unit(outward), false);
        }
    }

    /// <summary>Points along an outline, with each arc split into steps of at most <paramref name="stepDegrees"/>.</summary>
    public static IEnumerable<P2> OutlinePoints(IReadOnlyList<OutlineSegment> segments, double stepDegrees)
    {
        foreach (var segment in segments)
        {
            if (!segment.IsArc)
            {
                yield return segment.Start;
                continue;
            }
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(segment.Sweep) * 180 / Math.PI / stepDegrees));
            for (var k = 0; k < steps; k++) yield return segment.At((double)k / steps);
        }
    }

    // ---- internals -------------------------------------------------------------------------

    private static MeshFace Face(Vec3[] points, Vec3 inside, bool crisp)
    {
        var normal = V.Newell(points);
        var centroid = V.Mean(points);
        if (V.Dot(normal, centroid - inside) < 0)
        {
            normal = V.Scale(normal, -1);
            points = points.Reverse().ToArray();
        }
        return new MeshFace(points, normal, crisp);
    }

    /// <summary>A profile's outline at both ends of its extrusion, as 3D arcs and lines.</summary>
    private static IEnumerable<Arc3> ProfileArcs(RecipePart part, Pose pose)
    {
        var plane = part.Plane!;
        var origin = ProfilePoint(plane, new P2(0, 0), 0);
        var u = pose.Turn(ProfilePoint(plane, new P2(1, 0), 0) - origin);
        var v = pose.Turn(ProfilePoint(plane, new P2(0, 1), 0) - origin);
        foreach (var w in new[] { part.FromM!.Value, part.ToM!.Value })
        foreach (var segment in Outline(part.OutlineM!, part.RadiusM))
            yield return segment.IsArc
                ? new Arc3(pose.Apply(ProfilePoint(plane, segment.Centre, w)), u, v, segment.Radius, segment.StartAngle, segment.Sweep)
                : Arc3.Segment(pose.Apply(ProfilePoint(plane, segment.Start, w)), pose.Apply(ProfilePoint(plane, segment.End, w)));
    }

    /// <summary>A full circle with the given centre, unit axis and radius.</summary>
    private static Arc3 Circle(Vec3 centre, Vec3 axis, double radius)
    {
        var u = V.Perpendicular(axis);
        return new Arc3(centre, u, V.Cross(axis, u), radius, 0, 2 * Math.PI);
    }

    /// <summary>A circular arc (or, with radius 0, a line from Centre to U) in 3D.</summary>
    private sealed record Arc3(Vec3 Centre, Vec3 U, Vec3 V3, double Radius, double Start, double Sweep)
    {
        public static Arc3 Segment(Vec3 from, Vec3 to) => new(from, to, Vec3.Zero, 0, 0, 0);

        private Vec3 At(double angle) => Centre + V.Scale(U, Radius * Math.Cos(angle)) + V.Scale(V3, Radius * Math.Sin(angle));

        /// <summary>Its end points, plus every point where it is furthest along a world axis.</summary>
        public IEnumerable<Vec3> Extremes()
        {
            if (Radius <= 0)
            {
                yield return Centre;
                yield return U;
                yield break;
            }
            yield return At(Start);
            yield return At(Start + Sweep);
            for (var axis = 0; axis < 3; axis++)
            {
                var peak = Math.Atan2(V3[axis], U[axis]);
                foreach (var candidate in new[] { peak, peak + Math.PI })
                    if (Within(candidate)) yield return At(candidate);
            }
        }

        public IEnumerable<Vec3> Samples(int count)
        {
            if (Radius <= 0)
            {
                yield return Centre;
                yield return U;
                yield break;
            }
            for (var k = 0; k < count; k++) yield return At(Start + Sweep * k / count);
        }

        private bool Within(double angle)
        {
            if (Math.Abs(Sweep) >= 2 * Math.PI - 1e-12) return true;
            var from = Sweep >= 0 ? Start : Start + Sweep;
            var delta = (angle - from) % (2 * Math.PI);
            if (delta < 0) delta += 2 * Math.PI;
            return delta <= Math.Abs(Sweep) + 1e-12;
        }
    }

    /// <summary>A placement: a turn about Z, then a move.</summary>
    private readonly record struct Pose(double Degrees, Vec3 Offset)
    {
        public Vec3 Turn(Vec3 p)
        {
            if (Degrees == 0) return p;
            var radians = Degrees * Math.PI / 180;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            return new Vec3(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos, p.Z);
        }

        public Vec3 Apply(Vec3 p) => Turn(p) + Offset;
    }

    /// <summary>A box part's tilt about X through its centre.</summary>
    private readonly record struct Tilt(double Degrees, Vec3 Centre)
    {
        public Vec3 Apply(Vec3 p)
        {
            if (Degrees == 0) return p;
            var radians = Degrees * Math.PI / 180;
            var dy = p.Y - Centre.Y;
            var dz = p.Z - Centre.Z;
            return new Vec3(p.X, Centre.Y + dy * Math.Cos(radians) - dz * Math.Sin(radians), Centre.Z + dy * Math.Sin(radians) + dz * Math.Cos(radians));
        }
    }
}

/// <summary>Small vector helpers kept off <see cref="Vec3"/>, which is also a JSON shape.</summary>
internal static class V
{
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static Vec3 Scale(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public static double Length(Vec3 a) => Math.Sqrt(Dot(a, a));
    public static Vec3 Unit(Vec3 a) => Scale(a, 1 / Length(a));
    public static Vec3 Mean(IReadOnlyCollection<Vec3> points) =>
        Scale(points.Aggregate(Vec3.Zero, (sum, p) => sum + p), 1.0 / points.Count);

    /// <summary>Any unit vector perpendicular to the unit vector <paramref name="axis"/>.</summary>
    public static Vec3 Perpendicular(Vec3 axis) =>
        Unit(Cross(axis, Math.Abs(axis.Z) < 0.9 ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0)));

    /// <summary>Newell's method: the (unnormalized) normal of a planar polygon, following its winding.</summary>
    public static Vec3 Newell(IReadOnlyList<Vec3> points)
    {
        double x = 0, y = 0, z = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            x += (a.Y - b.Y) * (a.Z + b.Z);
            y += (a.Z - b.Z) * (a.X + b.X);
            z += (a.X - b.X) * (a.Y + b.Y);
        }
        var n = new Vec3(x, y, z);
        var length = Length(n);
        return length < 1e-15 ? new Vec3(0, 0, 1) : Scale(n, 1 / length);
    }
}
