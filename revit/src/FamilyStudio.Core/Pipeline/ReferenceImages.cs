using FamilyStudio.Core.Json;
using FamilyStudio.Core.Model;

namespace FamilyStudio.Core.Pipeline;

/// <summary>The design reference: the user's own photo, or an image Codex generated from the brief.</summary>
public sealed record ReferenceImage(
    string Source,
    string Path,
    string Sha256,
    int Width,
    int Height,
    string? OriginalFileName,
    string? RevisedPrompt)
{
    public const string Upload = "upload";
    public const string Generated = "generated";
    public bool IsUpload => Source == Upload;
}

public sealed record ImageSize(int Width, int Height);

/// <summary>Imports, checks and hashes reference images. Pure .NET: it reads PNG and JPEG headers itself.</summary>
public static class ReferenceImages
{
    public const long MaxUploadBytes = 32L * 1024 * 1024;

    /// <summary>Checks a photo the user picked, without copying it. Throws with a user-facing message.</summary>
    public static ImageSize InspectUpload(string path)
    {
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg"))
            throw new ArgumentException("Choose a PNG or JPEG photo.");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) throw new ArgumentException("That photo could not be read.");
        if (info.Length > MaxUploadBytes) throw new ArgumentException("Choose a photo smaller than 32 MB.");
        var size = Measure(File.ReadAllBytes(path)) ?? throw new ArgumentException("That file is not a readable PNG or JPEG image.");
        if (size.Width < 64 || size.Height < 64 || size.Width > 16384 || size.Height > 16384 || (long)size.Width * size.Height > 40_000_000)
            throw new ArgumentException("Use a photo between 64 and 16384 pixels per side and at most 40 megapixels.");
        return size;
    }

    public static ReferenceImage ImportUpload(string path, SessionJournal journal)
    {
        var copy = CopyUpload(path, journal, $"reference-upload-{Guid.NewGuid():N}"[..27]);
        var reference = new ReferenceImage(ReferenceImage.Upload, copy.Path, copy.Sha256, copy.Size.Width, copy.Size.Height,
            System.IO.Path.GetFileName(path), null);
        journal.Write("reference_imported", new { reference.Sha256, reference.Width, reference.Height });
        return reference;
    }

    /// <summary>Checks a photo the user picked and copies it into the session folder as <paramref name="stem"/> plus its extension.</summary>
    public static (string Path, string Sha256, ImageSize Size) CopyUpload(string path, SessionJournal journal, string stem)
    {
        var size = InspectUpload(path);
        var bytes = File.ReadAllBytes(path);
        var destination = journal.PathFor(stem + (System.IO.Path.GetExtension(path).ToLowerInvariant() == ".png" ? ".png" : ".jpg"));
        File.WriteAllBytes(destination, bytes);
        return (destination, StudioJson.Sha256(bytes), size);
    }

    public static ReferenceImage ImportGenerated(StageResult stage, SessionJournal journal)
    {
        var image = stage.Image ?? throw new StudioProtocolException("The reference step returned no image.");
        var copy = SaveGenerated(image, journal, $"reference-generated-{stage.StageId}", "reference");
        var reference = new ReferenceImage(ReferenceImage.Generated, copy.Path, copy.Sha256, copy.Size.Width, copy.Size.Height, null, image.RevisedPrompt);
        journal.Write("reference_generated", new { stage.StageId, reference.Sha256, reference.Width, reference.Height });
        return reference;
    }

    /// <summary>
    /// Checks an image Codex generated and copies it into the session folder as <paramref name="stem"/>
    /// plus its own extension. <paramref name="noun"/> names it in errors ("reference", "render").
    /// </summary>
    public static (string Path, string Sha256, ImageSize Size) SaveGenerated(GeneratedImage image, SessionJournal journal, string stem, string noun)
    {
        var info = new FileInfo(image.SavedPath);
        if (!info.Exists || info.Length == 0 || info.Length > 64L * 1024 * 1024)
            throw new StudioProtocolException($"The generated {noun} image is missing or has an unexpected size.");
        var bytes = File.ReadAllBytes(image.SavedPath);
        var size = Measure(bytes) ?? throw new StudioProtocolException($"The generated {noun} is not a PNG or JPEG image.");
        if (size.Width < 256 || size.Height < 256)
            throw new StudioProtocolException($"The generated {noun} is too small to use. Try again.");
        var destination = journal.PathFor(stem + (bytes[0] == 0xFF ? ".jpg" : ".png"));
        File.WriteAllBytes(destination, bytes);
        return (destination, StudioJson.Sha256(bytes), size);
    }

    /// <summary>Reads pixel dimensions from PNG or JPEG bytes, or returns null for anything else.</summary>
    public static ImageSize? Measure(byte[] bytes)
    {
        if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[12] == (byte)'I' && bytes[13] == (byte)'H' && bytes[14] == (byte)'D' && bytes[15] == (byte)'R')
            return new(BigEndian32(bytes, 16), BigEndian32(bytes, 20));

        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return null;
        var i = 2;
        while (i + 9 < bytes.Length)
        {
            if (bytes[i] != 0xFF) { i++; continue; }
            var marker = bytes[i + 1];
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
            if (marker == 0xFF) { i++; continue; }
            var length = (bytes[i + 2] << 8) | bytes[i + 3];
            // Start-of-frame markers carry the size; C4, C8 and CC are tables, not frames.
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                return new((bytes[i + 7] << 8) | bytes[i + 8], (bytes[i + 5] << 8) | bytes[i + 6]);
            if (length < 2) return null;
            i += 2 + length;
        }
        return null;
    }

    private static int BigEndian32(byte[] b, int offset) => (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];
}
