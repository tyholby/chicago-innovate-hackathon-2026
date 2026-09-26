using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using FamilyStudio.Core.Model;
using FamilyStudio.Core.Validation;

namespace FamilyStudio.Revit.Native;

/// <summary>
/// Builds one native Furniture family (.rfa) from a validated recipe: free-form solids for every part
/// (see <see cref="ShapeSolids"/>), one named material per brief material, one type named by its
/// overall size. The saved family is measured back and rejected if it drifts from the recipe, or from
/// verified dimensions.
/// </summary>
internal static partial class FamilyBuilder
{
    /// <param name="Notes">Parts built with a fallback (for example sharp instead of rounded edges), for the session log.</param>
    public sealed record Result(string Path, Vec3 SizeM, string TypeName, IReadOnlyList<string> Notes);

    public static Result Create(Application app, string path, string familyName, FamilyRecipe recipe, StudioBrief brief, string? templateOverride)
    {
        var expected = RecipeRules.Validate(recipe, brief);
        var asset = brief.Assets.Single(a => a.Id == recipe.AssetId);
        // Named from the accepted size, not the measured one, so rebuilt revisions keep the same type.
        var typeName = TypeName(asset.SizeM);
        var familyDoc = app.NewFamilyDocument(ResolveTemplate(app, templateOverride))
            ?? throw new InvalidOperationException("Revit could not create a furniture family.");
        try
        {
            var solids = new List<Solid>();
            var notes = new List<string>();
            using (var transaction = new Transaction(familyDoc, "Family Studio: build family"))
            {
                transaction.Start();
                familyDoc.OwnerFamily.FamilyCategory = familyDoc.Settings.Categories.get_Item(BuiltInCategory.OST_Furniture);
                SetUpSingleType(familyDoc, typeName, asset);

                var materials = new Dictionary<string, ElementId>(StringComparer.Ordinal);
                foreach (var part in recipe.Solids)
                {
                    if (!materials.TryGetValue(part.MaterialId, out var materialId))
                    {
                        materialId = CreateMaterial(familyDoc, brief.Materials.Single(m => m.Id == part.MaterialId));
                        materials[part.MaterialId] = materialId;
                    }
                    foreach (var solid in ShapeSolids.Build(part, notes))
                    {
                        var form = FreeFormElement.Create(familyDoc, solid);
                        form.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.Set(materialId);
                        solids.Add(solid);
                    }
                }
                familyDoc.Regenerate();
                if (transaction.Commit() != TransactionStatus.Committed)
                    throw new InvalidOperationException("Revit did not commit the family geometry.");
            }

            var measured = Units.MeasureSolids(solids).Size;
            for (var axis = 0; axis < 3; axis++)
            {
                if (Math.Abs(measured[axis] - expected.Size[axis]) > RecipeRules.ContainmentToleranceM)
                    throw new InvalidOperationException("The built family does not match its validated geometry.");
                if (asset.DimensionsConfirmed && Math.Abs(measured[axis] - asset.SizeM[axis]) > Dimensions.ConfirmedToleranceM)
                    throw new InvalidOperationException("The built family differs from its verified dimensions by more than 2 mm.");
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            familyDoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            return new Result(path, measured, typeName, notes);
        }
        finally
        {
            if (familyDoc.IsValidObject) familyDoc.Close(false);
        }
    }

    /// <summary>A file-safe family name from the item's name, such as "Walnut Lounge Chair".</summary>
    public static string SafeName(string name)
    {
        var cleaned = UnsafeCharacters().Replace(name, " ").Trim();
        cleaned = Whitespace().Replace(cleaned, " ");
        if (cleaned.Length > 60) cleaned = cleaned[..60].TrimEnd();
        return cleaned.Length == 0 ? "Family Studio item" : cleaned;
    }

    public static string TypeName(Vec3 size) => string.Format(CultureInfo.InvariantCulture,
        "{0:0} x {1:0} x {2:0} mm", size.X * 1000, size.Y * 1000, size.Z * 1000);

    private static ElementId CreateMaterial(Document familyDoc, MaterialBrief brief)
    {
        var baseName = SafeName(brief.Name);
        var name = baseName;
        for (var i = 2; !Material.IsNameUnique(familyDoc, name); i++) name = $"{baseName} {i}";
        var id = Material.Create(familyDoc, name);
        var material = (Material)familyDoc.GetElement(id);
        material.Color = new Color((byte)brief.Rgb[0], (byte)brief.Rgb[1], (byte)brief.Rgb[2]);
        material.Shininess = 12;
        material.Smoothness = 30;
        return id;
    }

    private static void SetUpSingleType(Document familyDoc, string typeName, AssetBrief asset)
    {
        // Add our type before removing the template's: Revit refuses to delete a family's last type.
        var manager = familyDoc.FamilyManager;
        var ours = manager.Types.Cast<FamilyType>().FirstOrDefault(t => t.Name == typeName) ?? manager.NewType(typeName);
        foreach (var other in manager.Types.Cast<FamilyType>().Where(t => t.Name != typeName).ToArray())
        {
            manager.CurrentType = other;
            manager.DeleteCurrentType();
        }
        manager.CurrentType = ours;
        TrySet(manager, BuiltInParameter.ALL_MODEL_DESCRIPTION, asset.Description);
        TrySet(manager, BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, "Concept family generated by Family Studio.");
    }

    private static void TrySet(FamilyManager manager, BuiltInParameter id, string value)
    {
        try
        {
            if (manager.get_Parameter(id) is { StorageType: StorageType.String } parameter)
                manager.Set(parameter, value.Length > 250 ? value[..250] : value);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { /* optional metadata only */ }
    }

    private static string ResolveTemplate(Application app, string? templateOverride)
    {
        if (!string.IsNullOrWhiteSpace(templateOverride))
            return File.Exists(templateOverride) && templateOverride.EndsWith(".rft", StringComparison.OrdinalIgnoreCase)
                ? templateOverride
                : throw new FileNotFoundException($"FAMILY_STUDIO_FURNITURE_TEMPLATE points to \"{templateOverride}\", which is not a family template (.rft).");

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(app.FamilyTemplatePath)) roots.Add(app.FamilyTemplatePath);
        foreach (var baseFolder in new[] { Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ProgramFiles })
        foreach (var language in new[] { "English", "English_I" })
            roots.Add(System.IO.Path.Combine(Environment.GetFolderPath(baseFolder), "Autodesk", $"RVT {app.VersionNumber}", "Family Templates", language));

        foreach (var root in roots.Where(Directory.Exists))
        foreach (var name in new[] { "Metric Furniture.rft", "Furniture.rft" })
        {
            var candidate = System.IO.Path.Combine(root, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(
            "Revit's furniture family template was not found. Install Revit's family templates, or set FAMILY_STUDIO_FURNITURE_TEMPLATE in the .env file.");
    }

    [GeneratedRegex(@"[\\/:*?""<>|\[\]{};=,#]")]
    private static partial Regex UnsafeCharacters();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Reloads our own families in place, keeping instance positions and type parameter values.</summary>
internal sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
{
    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    {
        overwriteParameterValues = false;
        return true;
    }

    public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
    {
        source = FamilySource.Family;
        overwriteParameterValues = false;
        return true;
    }
}
