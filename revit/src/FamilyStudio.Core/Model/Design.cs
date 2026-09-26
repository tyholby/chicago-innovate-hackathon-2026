using System.Text.Json.Serialization;

namespace FamilyStudio.Core.Model;

/// <summary>Fixed limits shared by input validation, prompts and the native build.</summary>
public static class StudioLimits
{
    public const int CollectionSize = 7;
    public const int CollectionMaterials = 4;
    public const int MaxMaterialNotes = 4;
    public const int MaxMaterials = 16;
    public const int MaxQuantity = 16;
    public const int MaxParts = 60;

    /// <summary>The preview room is 8 m wide (X) and 6 m deep (Y), centred on the origin.</summary>
    public const double RoomHalfWidthM = 4;
    public const double RoomHalfDepthM = 3;
    public const double RoomHeightM = 4;
}

/// <summary>
/// What the user asked for, before any AI stage runs. A draft holds either one item
/// (a single furniture piece) or a seven-item collection with a shared style and four materials.
/// </summary>
public sealed record StudioDraft(
    string Style,
    string[] Assets,
    string[] Materials,
    string[] AssetNames,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReferenceImagePath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Vec3? KnownSizeM = null)
{
    [JsonIgnore] public bool IsSingleItem => Assets is { Length: 1 };

    [JsonIgnore]
    public bool IsComplete
    {
        get
        {
            try { Validate(); return true; }
            catch (ArgumentException) { return false; }
        }
    }

    [JsonIgnore]
    public bool HasContent =>
        ReferenceImagePath is not null || KnownSizeM is not null || !string.IsNullOrWhiteSpace(Style) ||
        Assets.Any(a => !string.IsNullOrWhiteSpace(a)) || Materials.Any(m => !string.IsNullOrWhiteSpace(m)) ||
        AssetNames.Any(n => !string.IsNullOrWhiteSpace(n));

    public static StudioDraft EmptySingleItem() => new("", new[] { "" }, Array.Empty<string>(), new[] { "" });

    public static StudioDraft EmptyCollection() => new("",
        Enumerable.Repeat("", StudioLimits.CollectionSize).ToArray(),
        Enumerable.Repeat("", StudioLimits.CollectionMaterials).ToArray(),
        Enumerable.Repeat("", StudioLimits.CollectionSize).ToArray());

    public void Validate()
    {
        if (Assets is null || Assets.Length is not (1 or StudioLimits.CollectionSize))
            throw new ArgumentException("Describe one item or a seven-item collection.");
        if (AssetNames is null || AssetNames.Length != Assets.Length)
            throw new ArgumentException("Each item needs a name slot, even when it is left blank.");
        if (Materials is null)
            throw new ArgumentException("Material notes are missing.");

        if (IsSingleItem)
        {
            if (string.IsNullOrWhiteSpace(Assets[0]) && ReferenceImagePath is null)
                throw new ArgumentException("Describe the item or add a reference photo.");
            if (Materials.Length > StudioLimits.MaxMaterialNotes || Materials.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Use up to four material notes, one per line, or leave materials automatic.");
            if (KnownSizeM is not null) Dimensions.Validate(KnownSizeM);
            return;
        }

        if (string.IsNullOrWhiteSpace(Style))
            throw new ArgumentException("Describe the room or collection and its style.");
        if (Assets.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Describe all seven items in the collection.");
        if (Materials.Length != StudioLimits.CollectionMaterials || Materials.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Describe all four collection materials.");
        if (ReferenceImagePath is not null || KnownSizeM is not null)
            throw new ArgumentException("Reference photos and known dimensions apply to a single item.");
    }

    /// <summary>
    /// True when both drafts ask for the same design. Known sizes match within 0.1 mm: the window shows
    /// sizes to 0.1 mm, so a reopened design would otherwise never match the inputs it was made from.
    /// </summary>
    public bool SameInputs(StudioDraft other) =>
        Style == other.Style &&
        Assets.SequenceEqual(other.Assets) &&
        Materials.SequenceEqual(other.Materials) &&
        AssetNames.SequenceEqual(other.AssetNames) &&
        ReferenceImagePath == other.ReferenceImagePath &&
        (KnownSizeM, other.KnownSizeM) switch
        {
            (null, null) => true,
            ({ } a, { } b) => Math.Abs(a.X - b.X) <= SizeMatchM && Math.Abs(a.Y - b.Y) <= SizeMatchM && Math.Abs(a.Z - b.Z) <= SizeMatchM,
            _ => false
        };

    private const double SizeMatchM = 0.0001;

    /// <summary>Applies the user's names to the brief, so the model never renames an item the user named.</summary>
    public StudioBrief ApplyNames(StudioBrief brief) => brief with
    {
        Assets = brief.Assets.Select((asset, i) =>
            i < AssetNames.Length && !string.IsNullOrWhiteSpace(AssetNames[i])
                ? asset with { Name = AssetNames[i].Trim() }
                : asset).ToArray()
    };
}

public sealed record MaterialBrief(string Id, string Name, string Description, int[] Rgb)
{
    [JsonIgnore] public string Hex => Rgb is { Length: 3 } ? $"#{Rgb[0]:X2}{Rgb[1]:X2}{Rgb[2]:X2}" : "#808080";
}

/// <summary>A physical part the user sized explicitly, such as "a 40 mm tabletop" or "a 450 mm pot".</summary>
public sealed record ComponentBrief(string Id, string Description, Vec3 SizeM);

public sealed record AssetBrief(
    string Id,
    string Name,
    string Description,
    int Quantity,
    Vec3 SizeM,
    bool FloorStanding,
    ComponentBrief[] Components,
    bool DimensionsConfirmed)
{
    public override string ToString() => Name;
}

/// <summary>
/// The structured, dimensioned brief every later stage works from. Dimensions are metres.
/// Once accepted it is frozen and hashed, so later stages cannot drift from what the user approved.
/// </summary>
public sealed record StudioBrief(string Title, string Style, AssetBrief[] Assets, MaterialBrief[] Materials)
{
    [JsonIgnore] public bool IsSingleItem => Assets is { Length: 1 };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Title) || string.IsNullOrWhiteSpace(Style))
            throw new ArgumentException("The brief needs a title and a style description.");
        if (Assets is null || Assets.Length is not (1 or StudioLimits.CollectionSize) ||
            !Assets.Select(a => a.Id).SequenceEqual(Enumerable.Range(1, Assets.Length).Select(i => $"a{i}")))
            throw new ArgumentException("The brief must hold one item or seven items, with IDs a1, a2 and so on in input order.");
        var minimumMaterials = Assets.Length == 1 ? 1 : StudioLimits.CollectionMaterials;
        if (Materials is null || Materials.Length < minimumMaterials || Materials.Length > StudioLimits.MaxMaterials ||
            !Materials.Select(m => m.Id).SequenceEqual(Enumerable.Range(1, Materials.Length).Select(i => $"m{i}")))
            throw new ArgumentException($"The brief needs {minimumMaterials} to {StudioLimits.MaxMaterials} materials with IDs m1, m2 and so on.");

        foreach (var asset in Assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Name))
                throw new ArgumentException($"Item {asset.Id} needs a name.");
            if (asset.Quantity < 1 || asset.Quantity > StudioLimits.MaxQuantity)
                throw new ArgumentException($"{asset.Name}: quantity must be between 1 and {StudioLimits.MaxQuantity}.");
            if (asset.SizeM is null || !asset.SizeM.IsFinite || asset.SizeM.X <= 0 || asset.SizeM.Y <= 0 || asset.SizeM.Z <= 0 ||
                asset.SizeM.X > 8 || asset.SizeM.Y > 8 || asset.SizeM.Z > StudioLimits.RoomHeightM)
                throw new ArgumentException($"{asset.Name}: overall size must be positive and at most 8 x 8 x 4 m.");
            if (asset.Components is null || asset.Components.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != asset.Components.Length)
                throw new ArgumentException($"{asset.Name}: component IDs must be unique.");
            foreach (var component in asset.Components)
                if (string.IsNullOrWhiteSpace(component.Id) || component.SizeM is null || !component.SizeM.IsFinite ||
                    component.SizeM.X <= 0 || component.SizeM.Y <= 0 || component.SizeM.Z <= 0)
                    throw new ArgumentException($"{asset.Name}: every sized component needs positive dimensions.");
        }

        foreach (var material in Materials)
            if (string.IsNullOrWhiteSpace(material.Name) || material.Rgb is not { Length: 3 } || material.Rgb.Any(c => c is < 0 or > 255))
                throw new ArgumentException($"Material {material.Id} needs a name and an RGB color from 0 to 255.");
    }
}
