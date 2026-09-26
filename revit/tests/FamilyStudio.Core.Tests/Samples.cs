using FamilyStudio.Core.Model;

namespace FamilyStudio.Core.Tests;

/// <summary>Small, hand-checked designs shared by the tests.</summary>
internal static class Samples
{
    public static readonly MaterialBrief Walnut = new("m1", "Walnut", "Frame and legs", new[] { 110, 74, 48 });
    public static readonly MaterialBrief Wool = new("m2", "Charcoal wool", "Cushions", new[] { 60, 60, 62 });

    /// <summary>A 600 x 500 x 750 mm stool: four legs and a seat.</summary>
    public static StudioBrief StoolBrief(bool confirmed = false) => new("Stool", "Simple timber stool",
        new[] { new AssetBrief("a1", "Stool", "Four legs and a square seat", 1, new Vec3(0.6, 0.5, 0.75), true, Array.Empty<ComponentBrief>(), confirmed) },
        new[] { Walnut, Wool });

    public static RecipePart[] StoolParts(double height = 0.75) => new[]
    {
        new RecipePart("seat", new Vec3(-0.3, -0.25, height - 0.05), new Vec3(0.3, 0.25, height), 0, "m2", null, false),
        Leg("leg-fl", -0.3, -0.25, height - 0.05),
        Leg("leg-fr", 0.25, -0.25, height - 0.05),
        Leg("leg-bl", -0.3, 0.2, height - 0.05),
        Leg("leg-br", 0.25, 0.2, height - 0.05)
    };

    private static RecipePart Leg(string name, double x, double y, double top) =>
        new(name, new Vec3(x, y, 0), new Vec3(x + 0.05, y + 0.05, top), 0, "m1", null, true);

    public static FamilyRecipe StoolRecipe() => new RecipeDraft("a1", StoolParts()).Compile(StoolBrief());

    /// <summary>A seven-item collection brief whose items are all simple 500 mm cubes, except a1 (a table).</summary>
    public static StudioBrief CollectionBrief() => new("Test room", "A test collection",
        Enumerable.Range(1, 7).Select(i => new AssetBrief($"a{i}", i == 1 ? "Table" : $"Item {i}", "Box", 1,
            i == 1 ? new Vec3(1.2, 0.8, 0.75) : new Vec3(0.5, 0.5, 0.5), i != 2, Array.Empty<ComponentBrief>(), false)).ToArray(),
        new[] { Walnut, Wool, new MaterialBrief("m3", "Brass", "Accents", new[] { 180, 150, 90 }), new MaterialBrief("m4", "Ivory", "Shades", new[] { 240, 235, 220 }) });

    public static FamilyRecipe Box(string assetId, Vec3 size, StudioBrief brief, bool floor = true) =>
        new RecipeDraft(assetId, new[]
        {
            new RecipePart("body", new Vec3(-size.X / 2, -size.Y / 2, 0), new Vec3(size.X / 2, size.Y / 2, size.Z), 0, "m1", null, floor)
        }).Compile(brief);

    /// <summary>A table: a top part named "top" on a single pedestal.</summary>
    public static FamilyRecipe Table(StudioBrief brief) => new RecipeDraft("a1", new[]
    {
        new RecipePart("top", new Vec3(-0.6, -0.4, 0.72), new Vec3(0.6, 0.4, 0.75), 0, "m1", null, false),
        new RecipePart("pedestal", new Vec3(-0.1, -0.1, 0), new Vec3(0.1, 0.1, 0.72), 0, "m1", null, true)
    }).Compile(brief);
}
