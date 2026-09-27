using FamilyStudio.Core.Codex;
using FamilyStudio.Core.Model;
using FamilyStudio.Core.Pipeline;
using FamilyStudio.Core.Prompts;

namespace FamilyStudio.Core.Tests;

public class ViewRenderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fs-render-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly ModelChoice Model = new("test-model", "low");

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>Answers the render stage with a generated image, recording the request.</summary>
    private sealed class DrawingAgent(string folder) : IStudioAgent
    {
        public readonly List<StageRequest> Requests = new();
        public bool ReturnNoImage;
        public event Action<StageProgress>? Progress;

        public async Task<StageResult> RunAsync(StageRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Progress?.Invoke(new StageProgress(request.Name, "s1", StagePhase.Drawing, TimeSpan.FromSeconds(1), 0, 0));
            if (ReturnNoImage) return new StageResult("s1", "No image.", null, null, "test-model", TimeSpan.FromSeconds(2));
            var path = Png(folder, $"generated-{Requests.Count}.png", 1536, 1024);
            await Task.Yield();
            return new StageResult("s1", "Rendered.", new GeneratedImage("item-1", path, "a revised prompt"), null, "test-model", TimeSpan.FromSeconds(42));
        }

        public void Dispose() { }
    }

    /// <summary>A PNG header with the given size: enough for every check, which only reads headers.</summary>
    private static string Png(string folder, string name, int width, int height)
    {
        Directory.CreateDirectory(folder);
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        BitConverter.GetBytes(width).Reverse().ToArray().CopyTo(bytes, 16);
        BitConverter.GetBytes(height).Reverse().ToArray().CopyTo(bytes, 20);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private (ViewRenderer Renderer, DrawingAgent Agent, ViewCapture Capture) Create()
    {
        var agent = new DrawingAgent(Path.Combine(_folder, "codex"));
        var journal = new SessionJournal(Path.Combine(_folder, "session"));
        var capture = new ViewCapture(Png(journal.DirectoryPath, "capture.png", 2048, 1152), "{3D}", "3D view", "Project1", 2048, 1152);
        return (new ViewRenderer(agent, journal), agent, capture);
    }

    [Fact]
    public void The_user_prompt_is_appended_as_additional_context()
    {
        Assert.Equal(RenderPrompts.Default, RenderPrompts.Compose(null));
        Assert.Equal(RenderPrompts.Default, RenderPrompts.Compose("   "));
        Assert.Equal(RenderPrompts.Default + " ADDITIONAL CONTEXT: warm evening light", RenderPrompts.Compose("  warm evening light \n"));
    }

    [Fact]
    public void Reference_images_are_described_before_the_additional_context()
    {
        var one = RenderPrompts.Compose("oak floors", referenceImages: 1);
        Assert.Contains("The second attached image is a reference image", one);
        Assert.EndsWith(" ADDITIONAL CONTEXT: oak floors", one);

        var three = RenderPrompts.Compose(null, referenceImages: 3);
        Assert.StartsWith(RenderPrompts.Default, three);
        Assert.Contains("Attached images 2 to 4 are reference images", three);
        Assert.Contains("never geometry, layout, camera or composition", three);
        Assert.DoesNotContain("ADDITIONAL CONTEXT", three);
    }

    [Fact]
    public void Render_prompts_have_no_em_dashes()
    {
        foreach (var text in new[] { RenderPrompts.Developer, RenderPrompts.Compose("x", 2), RenderPrompts.Compose("x", 1) })
            Assert.DoesNotContain((char)0x2014, text); // the em dash
    }

    [Fact]
    public void Renders_run_on_the_configured_or_default_model_at_low_effort()
    {
        var models = new[]
        {
            new CodexModel("other", "Other", "", false, "medium", new[] { "medium", "high" }),
            new CodexModel("main", "Main", "", true, "medium", new[] { "low", "medium", "high", "xhigh" })
        };
        Assert.Equal(new ModelChoice("main", "low"), ViewRenderer.ChooseModel(models, null));
        Assert.Equal(new ModelChoice("main", "low"), ViewRenderer.ChooseModel(models, "not-in-the-catalog"));
        Assert.Equal(new ModelChoice("other", "medium"), ViewRenderer.ChooseModel(models, "other"));
        Assert.Throws<StudioProtocolException>(() => ViewRenderer.ChooseModel(Array.Empty<CodexModel>(), null));
    }

    [Fact]
    public async Task A_render_sends_the_capture_first_then_copies_of_the_references()
    {
        var (renderer, agent, capture) = Create();
        var references = new[] { Png(Path.Combine(_folder, "mine"), "oak.png", 800, 600), Png(Path.Combine(_folder, "mine"), "sky.png", 640, 640) };

        var render = await renderer.RenderAsync(capture, "evening light", references, Model, CancellationToken.None);

        var request = Assert.Single(agent.Requests);
        Assert.Equal("render", request.Name);
        Assert.Equal(StageKind.Image, request.Kind);
        Assert.Equal(Model, request.Model);
        Assert.Equal(RenderPrompts.Developer, request.Instructions);
        Assert.Equal(RenderPrompts.Compose("evening light", 2), request.Prompt);
        Assert.Null(request.OutputSchema);
        Assert.Equal(3, request.Images.Count);
        Assert.Equal(capture.Path, request.Images[0]);
        Assert.All(request.Images.Skip(1), path => Assert.StartsWith(renderer.Journal.DirectoryPath, path));
        Assert.EndsWith("render-01-reference-1.png", request.Images[1]);
        Assert.EndsWith("render-01-reference-2.png", request.Images[2]);

        Assert.Equal(Path.Combine(renderer.Journal.DirectoryPath, "render-01.png"), render.Path);
        Assert.True(File.Exists(render.Path));
        Assert.Equal((1536, 1024), (render.Width, render.Height));
        Assert.Equal("a revised prompt", render.RevisedPrompt);
        Assert.Equal(TimeSpan.FromSeconds(42), render.Elapsed);
        Assert.Equal(request.Prompt, File.ReadAllText(Path.Combine(renderer.Journal.DirectoryPath, "render-01-prompt.txt")));

        var second = await renderer.RenderAsync(capture, null, Array.Empty<string>(), Model, CancellationToken.None);
        Assert.EndsWith("render-02.png", second.Path);
        Assert.Equal(RenderPrompts.Default, agent.Requests[1].Prompt);
        Assert.Single(agent.Requests[1].Images);
    }

    [Fact]
    public async Task Too_many_references_bad_files_and_missing_images_are_refused()
    {
        var (renderer, agent, capture) = Create();
        var many = Enumerable.Range(0, ViewRenderer.MaxReferenceImages + 1).Select(i => Png(Path.Combine(_folder, "mine"), $"r{i}.png", 300, 300)).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(capture, null, many, Model, CancellationToken.None));

        var text = Path.Combine(_folder, "mine", "notes.png");
        File.WriteAllText(text, "not an image");
        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(capture, null, new[] { text }, Model, CancellationToken.None));
        Assert.Empty(agent.Requests); // nothing is sent until every reference checks out

        agent.ReturnNoImage = true;
        await Assert.ThrowsAsync<StudioProtocolException>(() => renderer.RenderAsync(capture, null, Array.Empty<string>(), Model, CancellationToken.None));
    }

    [Fact]
    public void Render_progress_moves_forward_through_the_phases_and_only_completes_at_the_end()
    {
        var progress = new RenderProgress();
        progress.Reset();
        var seen = new List<double>();
        void At(StagePhase phase, double seconds)
        {
            progress.Update(new StageProgress("render", "s1", phase, TimeSpan.FromSeconds(seconds), 0, 0));
            seen.Add(progress.Percent);
        }

        At(StagePhase.Starting, 0);
        At(StagePhase.Thinking, 5);
        At(StagePhase.Writing, 8); // a note before the image counts as preparing
        Assert.InRange(progress.Percent, 5, 20);
        Assert.StartsWith("Preparing the render", progress.Text);

        At(StagePhase.Drawing, 10);
        At(StagePhase.Drawing, 40);
        At(StagePhase.Drawing, 70);
        Assert.InRange(progress.Percent, 70, 95);
        Assert.Equal("Rendering (1:10)", progress.Text);

        At(StagePhase.Drawing, 600);
        Assert.True(progress.Percent <= 95);
        At(StagePhase.Thinking, 601); // after the image, the model only writes its closing note
        Assert.StartsWith("Finishing", progress.Text);
        At(StagePhase.Starting, 602); // a late or out-of-order report never moves the bar back
        At(StagePhase.Finished, 603);
        Assert.Equal(97, progress.Percent);
        Assert.Equal(seen.OrderBy(p => p), seen);

        progress.Complete();
        Assert.Equal(100, progress.Percent);
    }
}
