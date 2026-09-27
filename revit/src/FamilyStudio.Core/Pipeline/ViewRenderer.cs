using FamilyStudio.Core.Codex;
using FamilyStudio.Core.Model;
using FamilyStudio.Core.Prompts;

namespace FamilyStudio.Core.Pipeline;

/// <summary>An image of what a Revit view shows, exported for rendering.</summary>
/// <param name="ViewKind">The kind of view, such as "3D view" or "Floor plan".</param>
public sealed record ViewCapture(string Path, string ViewName, string ViewKind, string DocumentName, int Width, int Height);

/// <summary>A finished render, and what produced it.</summary>
public sealed record ViewRender(string Path, int Width, int Height, string Prompt, string? RevisedPrompt, TimeSpan Elapsed, StageUsage? Usage);

/// <summary>
/// View2Render: turns a capture of the visible Revit viewport into a photorealistic image through
/// Codex image generation, optionally steered by the user's words and reference images. Each render
/// is one image stage in a fresh thread. The session folder keeps what it sent (the capture, copies of
/// the references and the prompt) and what came back.
/// </summary>
public sealed class ViewRenderer(IStudioAgent agent, SessionJournal journal)
{
    /// <summary>Reference images per render, besides the capture.</summary>
    public const int MaxReferenceImages = 8;

    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(8);

    private int _renders;

    public SessionJournal Journal => journal;

    /// <summary>
    /// The render stage only has to hand the brief and images to the image tool, so it runs on the
    /// configured model (or the catalog default) at low effort. The image model does the work.
    /// </summary>
    public static ModelChoice ChooseModel(IReadOnlyList<CodexModel> models, string? preferred)
    {
        var model = models.FirstOrDefault(m => m.Id == preferred) ?? models.FirstOrDefault(m => m.IsDefault) ?? models.FirstOrDefault()
            ?? throw new StudioProtocolException("Codex lists no models for this ChatGPT account.");
        return new ModelChoice(model.Id, model.Efforts.Contains("low") ? "low" : model.DefaultEffort);
    }

    public async Task<ViewRender> RenderAsync(ViewCapture capture, string? userPrompt, IReadOnlyList<string> references, ModelChoice model,
        CancellationToken cancellationToken)
    {
        if (references.Count > MaxReferenceImages) throw new ArgumentException($"Use up to {MaxReferenceImages} reference images.");
        if (!File.Exists(capture.Path)) throw new ArgumentException("The captured view is missing. Capture the view again.");
        var stem = $"render-{Interlocked.Increment(ref _renders):D2}";

        // Copies, so the session folder shows exactly what was sent, and a file moved or edited later changes nothing.
        var copies = references.Select((path, i) => ReferenceImages.CopyUpload(path, journal, $"{stem}-reference-{i + 1}").Path).ToArray();
        var prompt = RenderPrompts.Compose(userPrompt, copies.Length);
        journal.Artifact($"{stem}-prompt.txt", prompt);
        journal.Write("render_started", new { stem, promptVersion = RenderPrompts.Version, capture.ViewName, capture.Width, capture.Height,
            references = copies.Length, model });

        var result = await agent.RunAsync(new StageRequest("render", StageKind.Image, model, RenderPrompts.Developer, prompt,
            new[] { capture.Path }.Concat(copies).ToArray(), null, Timeout), cancellationToken).ConfigureAwait(false);
        var image = result.Image ?? throw new StudioProtocolException("The render step returned no image.");
        var saved = ReferenceImages.SaveGenerated(image, journal, stem, "render");
        journal.Write("render_finished", new { stem, saved.Sha256, saved.Size.Width, saved.Size.Height, elapsedMs = (long)result.Elapsed.TotalMilliseconds });
        return new ViewRender(saved.Path, saved.Size.Width, saved.Size.Height, prompt, image.RevisedPrompt, result.Elapsed, result.Usage);
    }
}

/// <summary>
/// The render's progress bar. Codex reports phases rather than percentages, so the bar eases through
/// each phase over its usual length, never moves backwards, and reaches 100 only when the render is in.
/// </summary>
public sealed class RenderProgress
{
    private static readonly TimeSpan UsualPreparing = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UsualDrawing = TimeSpan.FromSeconds(60);
    private TimeSpan? _drawingFrom;

    /// <summary>0 to 100.</summary>
    public double Percent { get; private set; }

    public string Text { get; private set; } = "";

    public void Reset()
    {
        _drawingFrom = null;
        Percent = 0;
        Text = "Starting";
    }

    public void Update(StageProgress progress)
    {
        var clock = $"{(int)progress.Elapsed.TotalMinutes}:{progress.Elapsed.Seconds:00}";
        var (value, text) = progress.Phase switch
        {
            StagePhase.Finished => (Percent, Text),
            StagePhase.Drawing => (Drawing(progress.Elapsed), $"Rendering ({clock})"),
            // Once the image is drawn, whatever the model does next is its closing note.
            _ when _drawingFrom is not null => (97.0, $"Finishing ({clock})"),
            StagePhase.Starting => (3.0, $"Sending the view to ChatGPT ({clock})"),
            // Thinking, or a note the model writes before it draws.
            _ => (5 + 15 * Ease(progress.Elapsed, UsualPreparing), $"Preparing the render ({clock})")
        };
        Percent = Math.Max(Percent, Math.Min(value, 99));
        Text = progress.Note ?? text;
    }

    public void Complete()
    {
        Percent = 100;
        Text = "Done";
    }

    private double Drawing(TimeSpan elapsed)
    {
        _drawingFrom ??= elapsed;
        return 20 + 75 * Ease(elapsed - _drawingFrom.Value, UsualDrawing);
    }

    /// <summary>0 at the start of a phase, 0.8 after its usual length, and closer to 1 after that.</summary>
    private static double Ease(TimeSpan elapsed, TimeSpan usual) => 1 - Math.Exp(-1.6 * Math.Max(0, elapsed.TotalSeconds) / usual.TotalSeconds);
}
