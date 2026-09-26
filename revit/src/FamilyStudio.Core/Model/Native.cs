namespace FamilyStudio.Core.Model;

/// <summary>A family the host built and loaded, with its measured overall size.</summary>
public sealed record FamilyReceipt(
    string AssetId,
    string FamilyName,
    string TypeName,
    string RfaPath,
    string FamilyUniqueId,
    string SymbolUniqueId,
    int SolidParts,
    Vec3 SizeM,
    int Revision);

/// <summary>A placed instance, read back from the model after the transaction committed.</summary>
public sealed record InstanceReceipt(
    string Key,
    string AssetId,
    string UniqueId,
    Vec3 PositionM,
    double RotationDegrees,
    Vec3 BoundsMinM,
    Vec3 BoundsMaxM);

/// <summary>An exported view image and how it was framed.</summary>
public sealed record CaptureReceipt(
    string ViewKey,
    string ViewUniqueId,
    string Path,
    int Width,
    int Height,
    string Camera);

/// <summary>The native state of the preview room at one moment, read back from Revit.</summary>
public sealed record NativeSnapshot(
    string DocumentKey,
    string DocumentTitle,
    FamilyReceipt[] Families,
    InstanceReceipt[] Instances,
    CaptureReceipt[] Captures,
    long ChangeStamp)
{
    public static readonly string[] CaptureViews = { "plan", "front-left", "front-right" };
}

public sealed record ProjectChoice(string Key, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Guards against applying a proposal to a room that changed while the proposal was being prepared.</summary>
public sealed record NativeExpectation(string DocumentKey, long ChangeStamp)
{
    public void Verify(NativeSnapshot current)
    {
        if (current.DocumentKey != DocumentKey || current.ChangeStamp != ChangeStamp)
            throw new StudioEvidenceException("The preview room changed while this proposal was being prepared. Review the current state and try again.");
    }
}
