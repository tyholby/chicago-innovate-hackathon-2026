using FamilyStudio.Core.Model;
using FamilyStudio.Core.Validation;

namespace FamilyStudio.Core.Tests;

public class ShapesTests
{
    private const double Tight = 1e-6;

    private static RecipePart Cylinder(Vec3 a, Vec3 b, double r0, double? r1 = null, bool floor = false) =>
        new("c", null, null, 0, "m1", null, floor, PartShapes.Cylinder, r0, r1, new[] { a, b });

    private static void AssertBox(Box3 expected, Box3 actual, double tolerance)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            Assert.InRange(actual.Min[axis], expected.Min[axis] - tolerance, expected.Min[axis] + tolerance);
            Assert.InRange(actual.Max[axis], expected.Max[axis] - tolerance, expected.Max[axis] + tolerance);
        }
    }

    // An independent reference: points on a circle, from a basis built here rather than by Shapes.
    private static IEnumerable<Vec3> CirclePoints(Vec3 centre, Vec3 axis, double radius, int count = 7200)
    {
        var length = Math.Sqrt(axis.X * axis.X + axis.Y * axis.Y + axis.Z * axis.Z);
        var n = new Vec3(axis.X / length, axis.Y / length, axis.Z / length);
        var helper = Math.Abs(n.X) < 0.5 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
        var u = Unit(Cross(n, helper));
        var v = Cross(n, u);
        for (var k = 0; k < count; k++)
        {
            var t = 2 * Math.PI * k / count;
            yield return new Vec3(centre.X + radius * (Math.Cos(t) * u.X + Math.Sin(t) * v.X),
                centre.Y + radius * (Math.Cos(t) * u.Y + Math.Sin(t) * v.Y),
                centre.Z + radius * (Math.Cos(t) * u.Z + Math.Sin(t) * v.Z));
        }
    }

    private static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static Vec3 Unit(Vec3 a) { var l = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z); return new(a.X / l, a.Y / l, a.Z / l); }

    [Fact]
    public void An_upright_cylinder_is_bounded_by_its_radius()
    {
        var bounds = Shapes.Bounds(Cylinder(new Vec3(0.1, 0.2, 0), new Vec3(0.1, 0.2, 0.7), 0.05));
        AssertBox(new Box3(new Vec3(0.05, 0.15, 0), new Vec3(0.15, 0.25, 0.7)), bounds, Tight);
    }

    [Fact]
    public void A_splayed_tapered_leg_is_bounded_exactly_by_its_end_circles()
    {
        var a = new Vec3(-0.33, -0.36, 0.01);
        var b = new Vec3(-0.30, -0.32, 0.30);
        var axis = b - a;
        var reference = Box3.Of(CirclePoints(a, axis, 0.014).Concat(CirclePoints(b, axis, 0.020)));
        AssertBox(reference, Shapes.Bounds(Cylinder(a, b, 0.014, 0.020)), 1e-6);
    }

    [Fact]
    public void Placement_turns_are_measured_exactly()
    {
        var part = Cylinder(new Vec3(0.3, 0, 0), new Vec3(0.3, 0.4, 0.2), 0.03);
        var turned = Shapes.Bounds(part, 30, new Vec3(1, 2, 0));
        double Turn(Vec3 p, out double y) { var r = Math.PI / 6; y = p.X * Math.Sin(r) + p.Y * Math.Cos(r) + 2; return p.X * Math.Cos(r) - p.Y * Math.Sin(r) + 1; }
        var axis = part.PointsM![1] - part.PointsM[0];
        var points = CirclePoints(part.PointsM[0], axis, 0.03).Concat(CirclePoints(part.PointsM[1], axis, 0.03))
            .Select(p => { var x = Turn(p, out var y); return new Vec3(x, y, p.Z); });
        AssertBox(Box3.Of(points), turned, 1e-6);
    }

    [Fact]
    public void Spheres_and_tubes_grow_their_points_by_the_radius()
    {
        var sphere = new RecipePart("s", null, null, 0, "m1", null, false, PartShapes.Sphere, 0.02, null, new[] { new Vec3(0, 0, 0.5) });
        AssertBox(new Box3(new Vec3(-0.02, -0.02, 0.48), new Vec3(0.02, 0.02, 0.52)), Shapes.Bounds(sphere), Tight);

        var tube = new RecipePart("t", null, null, 0, "m1", null, false, PartShapes.Tube, 0.01, null,
            new[] { new Vec3(0, 0, 0.1), new Vec3(0.2, 0, 0.3), new Vec3(0.2, 0.4, 0.3) });
        AssertBox(new Box3(new Vec3(-0.01, -0.01, 0.09), new Vec3(0.21, 0.41, 0.31)), Shapes.Bounds(tube), Tight);
    }

    [Fact]
    public void A_tilted_rounded_box_is_its_core_grown_by_the_radius()
    {
        var part = new RecipePart("cushion", new Vec3(-0.3, 0.2, 0.4), new Vec3(0.3, 0.32, 0.8), -15, "m2", null, false, RadiusM: 0.05);
        // Reference: sample the core box and push each sample out by the radius in many directions.
        var core = Shapes.BoxCorners(part.MinM!, part.MaxM!, part.TiltDegrees, part.RadiusM).ToArray();
        var directions = new List<Vec3>();
        for (var i = 0; i <= 90; i++)
        for (var j = 0; j < 180; j++)
        {
            double polar = Math.PI * i / 90, azimuth = 2 * Math.PI * j / 180;
            directions.Add(new Vec3(Math.Sin(polar) * Math.Cos(azimuth), Math.Sin(polar) * Math.Sin(azimuth), Math.Cos(polar)));
        }
        var reference = Box3.Of(core.SelectMany(c => directions.Select(d => new Vec3(c.X + 0.05 * d.X, c.Y + 0.05 * d.Y, c.Z + 0.05 * d.Z))));
        AssertBox(reference, Shapes.Bounds(part), 1e-5);

        // Rounding pulls the tilted corners in: the sharp box would reach further.
        var sharp = Shapes.Bounds(part with { RadiusM = 0 });
        Assert.True(Shapes.Bounds(part).Max.Z < sharp.Max.Z - 0.005);
    }

    [Fact]
    public void Rounded_outlines_are_continuous_and_their_bounds_exact()
    {
        var triangle = new[] { new[] { 0.0, 0.0 }, new[] { 0.6, 0.0 }, new[] { 0.3, 0.5 } };
        var segments = Shapes.Outline(triangle, 0.05);
        Assert.Equal(6, segments.Count); // three edges and three fillets
        for (var i = 0; i < segments.Count; i++)
        {
            var end = segments[i].At(1);
            var next = segments[(i + 1) % segments.Count].Start;
            Assert.InRange((end - next).Length, 0, 1e-9);
        }

        var part = new RecipePart("p", null, null, 0, "m1", null, false, PartShapes.Profile, 0.05, null, null, ProfilePlanes.Front, triangle, -0.02, 0.02);
        var samples = segments.SelectMany(s => Enumerable.Range(0, 2001).Select(k => s.At(k / 2000.0))).ToArray();
        var bounds = Shapes.Bounds(part);
        Assert.InRange(bounds.Max.Z, samples.Max(p => p.V) - 1e-7, samples.Max(p => p.V) + 1e-9); // front: v is Z
        Assert.True(bounds.Max.Z < 0.5 - 0.01, "the rounded apex sits below the sharp one");
        Assert.InRange(bounds.Min.Y, -0.02 - Tight, -0.02 + Tight);
        Assert.InRange(bounds.Max.Y, 0.02 - Tight, 0.02 + Tight);
    }

    [Fact]
    public void A_rounded_rectangle_keeps_its_extremes()
    {
        var rectangle = new[] { new[] { 0.0, 0.0 }, new[] { 0.4, 0.0 }, new[] { 0.4, 0.3 }, new[] { 0.0, 0.3 } };
        var part = new RecipePart("p", null, null, 0, "m1", null, false, PartShapes.Profile, 0.05, null, null, ProfilePlanes.Side, rectangle, -0.1, 0.1);
        AssertBox(new Box3(new Vec3(-0.1, 0, 0), new Vec3(0.1, 0.4, 0.3)), Shapes.Bounds(part), Tight);
        var clockwise = rectangle.Reverse().ToArray();
        AssertBox(Shapes.Bounds(part), Shapes.Bounds(part with { OutlineM = clockwise }), Tight);
    }

    [Fact]
    public void Mirrored_parts_are_built_twice_and_measured_as_a_pair()
    {
        var parts = Samples.ChairParts();
        var solids = Shapes.Expand(parts).ToArray();
        Assert.Equal(parts.Length + parts.Count(p => p.Mirror), solids.Length);
        var arm = solids.Single(p => p.Name == "arm");
        var copy = solids.Single(p => p.Name == "arm mirrored");
        Assert.Equal(arm.PointsM!.Select(p => -p.X), copy.PointsM!.Select(p => p.X));
        Assert.False(copy.Mirror);

        var bounds = RecipeRules.Bounds(parts);
        Assert.InRange(bounds.Min.X, -bounds.Max.X - Tight, -bounds.Max.X + Tight); // symmetric about X = 0
    }

    [Fact]
    public void A_side_profile_mirrors_its_extrusion_range()
    {
        var part = Samples.ChairParts().Single(p => p.Name == "back shell") with { FromM = 0.1, ToM = 0.3 };
        var copy = Shapes.Mirrored(part);
        Assert.Equal(-0.3, copy.FromM);
        Assert.Equal(-0.1, copy.ToM);
        Assert.Equal(part.OutlineM, copy.OutlineM);
    }

    [Fact]
    public void Splayed_legs_are_settled_onto_the_floor()
    {
        var leg = Cylinder(new Vec3(-0.33, -0.36, 0), new Vec3(-0.30, -0.32, 0.30), 0.014, 0.020, floor: true);
        Assert.True(Shapes.Bounds(leg).Min.Z < -0.001, "a tilted foot dips below the floor before settling");
        var settled = Shapes.Settle(leg);
        Assert.InRange(Shapes.Bounds(settled).Min.Z, -1e-12, 1e-12);

        var floating = Cylinder(new Vec3(0, 0, 0.5), new Vec3(0, 0, 0.9), 0.02, floor: true);
        Assert.Same(floating, Shapes.Settle(floating)); // far from the floor: left for validation to report
    }

    [Fact]
    public void The_chair_compiles_with_every_shape()
    {
        var recipe = new RecipeDraft("a1", Samples.ChairParts()).Compile(Samples.ChairBrief());
        Assert.InRange(recipe.EnvelopeMinM.Z, -1e-9, 1e-9);
        Assert.Equal(Samples.ChairParts().Length, recipe.Parts.Length);
        Assert.Equal(11, recipe.Solids.Count()); // eight parts, three of them mirrored
    }

    [Theory]
    [InlineData("cylinder_points")]
    [InlineData("profile_outline")]
    [InlineData("box_radius")]
    [InlineData("unknown_shape")]
    [InlineData("duplicate_part")]
    [InlineData("tube_radius")]
    public void Malformed_shapes_are_rejected_with_a_specific_code(string code)
    {
        var parts = Samples.ChairParts().ToList();
        switch (code)
        {
            case "cylinder_points":
                parts[0] = parts[0] with { PointsM = new[] { new Vec3(0, 0, 0) } };
                break;
            case "profile_outline":
                parts[6] = parts[6] with { OutlineM = new[] { new[] { 0.0, 0.0 }, new[] { 0.4, 0.4 }, new[] { 0.4, 0.0 }, new[] { 0.0, 0.4 } } }; // a bow tie
                break;
            case "box_radius":
                parts[2] = parts[2] with { RadiusM = 0.04 }; // the frame is 60 mm thick
                break;
            case "unknown_shape":
                parts[7] = parts[7] with { Shape = "cone" };
                break;
            case "duplicate_part":
                parts.Add(parts[5] with { Name = "arm mirrored", Mirror = false });
                break;
            case "tube_radius":
                parts[5] = parts[5] with { RadiusM = 0.001 };
                break;
        }
        var error = Assert.Throws<StudioValidationException>(() => new RecipeDraft("a1", parts.ToArray()).Compile(Samples.ChairBrief()));
        Assert.Contains(error.Issues, i => i.Code == code);
    }

    [Fact]
    public void Level_boxes_and_upright_cylinders_offer_support_surfaces()
    {
        var box = new RecipePart("top", new Vec3(-0.6, -0.4, 0.70), new Vec3(0.6, 0.4, 0.74), 0, "m1", null, false, RadiusM: 0.01);
        var surface = Assert.Single(Shapes.Surfaces(box));
        AssertBox(new Box3(new Vec3(-0.59, -0.39, 0.74), new Vec3(0.59, 0.39, 0.74)), new Box3(surface.MinM, surface.MaxM), Tight);

        var round = Cylinder(new Vec3(0, 0, 0.70), new Vec3(0, 0, 0.74), 0.5);
        var disc = Assert.Single(Shapes.Surfaces(round));
        var half = 0.5 / Math.Sqrt(2);
        AssertBox(new Box3(new Vec3(-half, -half, 0.74), new Vec3(half, half, 0.74)), new Box3(disc.MinM, disc.MaxM), Tight);

        Assert.Empty(Shapes.Surfaces(box with { TiltDegrees = 10 }));
        Assert.Empty(Shapes.Surfaces(Cylinder(new Vec3(0, 0, 0.3), new Vec3(0.1, 0, 0.7), 0.02)));
    }

    [Fact]
    public void A_pedestal_gives_a_stable_footprint()
    {
        var pedestal = Cylinder(new Vec3(0, 0, 0), new Vec3(0, 0, 0.5), 0.12, floor: true);
        var feet = Shapes.ContactPoints(pedestal).Where(p => Math.Abs(p.Z) < 1e-9).Distinct().ToArray();
        Assert.True(feet.Length >= 3);
        Assert.All(feet, p => Assert.InRange(Math.Sqrt(p.X * p.X + p.Y * p.Y), 0.12 - 1e-9, 0.12 + 1e-9));
    }

    [Fact]
    public void Drawing_meshes_stay_inside_the_exact_bounds()
    {
        foreach (var part in Shapes.Expand(Samples.ChairParts().Select(Shapes.Settle)))
        {
            var bounds = Shapes.Bounds(part);
            foreach (var face in Shapes.Mesh(part))
            {
                Assert.All(face.Points, p =>
                {
                    for (var axis = 0; axis < 3; axis++)
                        Assert.InRange(p[axis], bounds.Min[axis] - 1e-9, bounds.Max[axis] + 1e-9);
                });
                Assert.InRange(Math.Sqrt(face.Normal.X * face.Normal.X + face.Normal.Y * face.Normal.Y + face.Normal.Z * face.Normal.Z), 1 - 1e-9, 1 + 1e-9);
            }
        }
    }

    [Fact]
    public void Rounded_box_meshes_face_outward()
    {
        var part = new RecipePart("cushion", new Vec3(-0.3, -0.3, 0.3), new Vec3(0.3, 0.3, 0.45), 0, "m2", null, false, RadiusM: 0.05);
        var centre = new Vec3(0, 0, 0.375);
        Assert.All(Shapes.Mesh(part), face =>
        {
            var c = new Vec3(face.Points.Average(p => p.X), face.Points.Average(p => p.Y), face.Points.Average(p => p.Z));
            var d = c - centre;
            Assert.True(d.X * face.Normal.X + d.Y * face.Normal.Y + d.Z * face.Normal.Z > 0);
        });
    }
}
