using FamilyStudio.Core.Json;
using FamilyStudio.Core.Model;
using FamilyStudio.Core.Prompts;

namespace FamilyStudio.Core.Pipeline;

/// <summary>
/// The contract a build works from: a frozen brief and its reference image, both hashed at
/// acceptance. Every build step re-verifies them, so an edited file can never slip into a build.
/// </summary>
public sealed class AcceptedDesign
{
    private readonly string _briefJson;

    public StudioBrief Brief => StudioJson.Read<StudioBrief>(_briefJson);
    public string BriefSha256 { get; }
    public ReferenceImage Reference { get; }

    public AcceptedDesign(StudioBrief brief, ReferenceImage reference)
    {
        brief.Validate();
        _briefJson = StudioJson.Write(brief);
        BriefSha256 = StudioJson.Sha256(_briefJson);
        Reference = reference;
        Verify();
    }

    public void Verify()
    {
        if (!File.Exists(Reference.Path) || StudioJson.Sha256(File.ReadAllBytes(Reference.Path)) != Reference.Sha256)
            throw new StudioEvidenceException("The accepted reference image changed or is missing. Accept the design again.");
    }
}

/// <summary>What is saved next to a session so an accepted design can be reopened later.</summary>
public sealed record AcceptedDesignFile(string PromptVersion, StudioBrief Brief, string BriefSha256, ReferenceImage Reference, StudioDraft Draft)
{
    public const string FilePrefix = "accepted-";

    public static string Save(SessionJournal journal, AcceptedDesign design, StudioDraft draft)
    {
        var file = new AcceptedDesignFile(StudioPrompts.Version, design.Brief, design.BriefSha256, design.Reference, draft);
        return journal.Artifact($"{FilePrefix}{DateTime.Now:yyyyMMdd-HHmmss}.json", StudioJson.Write(file));
    }

    /// <summary>Reads a saved design and checks that its brief and image are exactly what was accepted.</summary>
    public static AcceptedDesignFile Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 16 * 1024 * 1024) throw new ArgumentException("Choose an accepted design file saved by Family Studio.");
        var file = StudioJson.Read<AcceptedDesignFile>(File.ReadAllText(path));
        if (StudioJson.Hash(file.Brief) != file.BriefSha256)
            throw new StudioEvidenceException("This design's brief was edited after it was accepted.");
        var image = file.Reference.Path;
        if (!File.Exists(image))
        {
            // Sessions can be moved as a folder: fall back to the image beside the file.
            image = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(image));
            if (!File.Exists(image)) throw new StudioEvidenceException("This design's reference image is missing.");
        }
        if (StudioJson.Sha256(File.ReadAllBytes(image)) != file.Reference.Sha256)
            throw new StudioEvidenceException("This design's reference image changed after it was accepted.");
        return file with { Reference = file.Reference with { Path = image } };
    }
}
