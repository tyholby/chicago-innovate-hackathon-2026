using FamilyStudio.Core.Json;
using FamilyStudio.Core.Model;
using FamilyStudio.Core.Pipeline;
using FamilyStudio.Core.Prompts;
using FamilyStudio.Core.Validation;

namespace FamilyStudio.Core.Tests;

public class SessionFlowTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fs-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly StageSettings Settings = new(new ModelChoice("test-model", "low"), Fidelity.Concept);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// Answers each stage from a script keyed by stage name, recording every request. Stages may run
    /// at once (a collection plans in parallel), so it is thread-safe and records the most seen at once.
    /// </summary>
    private sealed class ScriptedAgent(string folder) : IStudioAgent
    {
        public readonly List<StageRequest> Requests = new();
        public readonly Dictionary<string, Queue<Func<StageRequest, string>>> Script = new();
        public Func<CancellationToken, Task>? BeforeEachStage;
        public Action<StageRequest>? DuringStage;
        public TimeSpan RecipeDelay;
        public int MaxRunning;
        private int _running;
        public event Action<StageProgress>? Progress;

        public void Report(StageProgress progress) => Progress?.Invoke(progress);

        public async Task<StageResult> RunAsync(StageRequest request, CancellationToken cancellationToken)
        {
            int count;
            lock (Requests)
            {
                Requests.Add(request);
                count = Requests.Count;
                MaxRunning = Math.Max(MaxRunning, ++_running);
            }
            try
            {
                if (BeforeEachStage is not null) await BeforeEachStage(cancellationToken);
                if (RecipeDelay > TimeSpan.Zero && request.Name.StartsWith("recipe-", StringComparison.Ordinal)) await Task.Delay(RecipeDelay, cancellationToken);
                if (request.Kind == StageKind.Image)
                {
                    var path = Path.Combine(folder, $"generated-{count}.png");
                    var png = new byte[64];
                    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0x06, 0, 0, 0, 0x04, 0 }.CopyTo(png, 0);
                    Directory.CreateDirectory(folder);
                    await File.WriteAllBytesAsync(path, png, cancellationToken);
                    return new StageResult("img", "", new GeneratedImage("item-1", path, null), null, "test-model", TimeSpan.Zero);
                }
                DuringStage?.Invoke(request);
                Func<StageRequest, string> next;
                lock (Script) next = Script[request.Name].Dequeue();
                return new StageResult($"s{count}", next(request), null, null, "test-model", TimeSpan.Zero);
            }
            finally
            {
                lock (Requests) _running--;
            }
        }

        public void Dispose() { }
    }

    private sealed class FakeHost : IStudioHost
    {
        public readonly List<BuildProposal> Applied = new();
        private StudioBrief? _brief;
        private readonly Dictionary<string, FamilyReceipt> _families = new();
        private readonly Dictionary<string, InstanceReceipt> _instances = new();
        private long _stamp;
        public bool Misplace;
        public event Action? DocumentUnavailable;
        public Task WhenIdle => Task.CompletedTask;
        public void BindDesign(StudioBrief brief) { _brief = brief; _families.Clear(); _instances.Clear(); }
        public Task<NativeSnapshot> EnsureRoomAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot());
        public Task<NativeSnapshot> ApplyAsync(BuildProposal proposal, NativeExpectation? expected, CancellationToken cancellationToken)
        {
            expected?.Verify(Snapshot());
            Applied.Add(proposal);
            foreach (var r in proposal.Recipes)
                _families[r.AssetId] = new FamilyReceipt(r.AssetId, r.AssetId, "t", "x.rfa", r.AssetId, r.AssetId, r.Parts.Length, RecipeRules.Validate(r, _brief!).Size, 1);
            foreach (var p in proposal.Placements)
            {
                var at = Misplace ? p.PositionM + new Vec3(0, 0, 0.1) : p.PositionM;
                _instances[p.Key] = new InstanceReceipt(p.Key, p.AssetId, p.Key, at, p.RotationDegrees, at, at);
            }
            _stamp++;
            return Task.FromResult(Snapshot());
        }
        public Task<NativeSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot() with
        {
            Captures = NativeSnapshot.CaptureViews.Select(v => new CaptureReceipt(v, v, "x.png", 10, 10, "")).ToArray()
        });
        public Task<ProjectChoice[]> ListProjectsAsync(CancellationToken cancellationToken) => Task.FromResult(Array.Empty<ProjectChoice>());
        public Task LoadFamiliesAsync(string projectKey, IReadOnlyList<string> assetIds, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShowRoomAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloseAsync() => Task.CompletedTask;
        public void Dispose() { }

        /// <summary>The user closes the preview room document.</summary>
        public void CloseRoom()
        {
            _families.Clear();
            _instances.Clear();
            _stamp = 0;
            DocumentUnavailable?.Invoke();
        }

        private NativeSnapshot Snapshot() => new("room", "Room", _families.Values.ToArray(), _instances.Values.ToArray(), Array.Empty<CaptureReceipt>(), _stamp);
    }

    private (StudioSession Session, ScriptedAgent Agent, FakeHost Host) Create()
    {
        var agent = new ScriptedAgent(_folder);
        var host = new FakeHost();
        return (new StudioSession(agent, host, new SessionJournal(Path.Combine(_folder, "session"))), agent, host);
    }

    private static StudioDraft StoolDraft(Vec3? size = null) =>
        new("", new[] { "A four-legged timber stool" }, Array.Empty<string>(), new[] { "" }, null, size);

    [Fact]
    public async Task A_single_item_goes_from_words_to_a_centred_native_family()
    {
        var (session, agent, host) = Create();
        agent.Script["brief"] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(Samples.StoolBrief()) });
        agent.Script["recipe-a1"] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(new RecipeDraft("a1", Samples.StoolParts())) });

        var draft = StoolDraft();
        await session.GenerateAsync(draft, Settings);
        Assert.Equal(StudioState.Review, session.State);
        Assert.Equal(ReferenceImage.Generated, session.Reference!.Source);

        session.Accept(draft);
        await session.BuildAsync(Settings, reviewAfterBuild: false);

        Assert.Equal(StudioState.Built, session.State);
        var placement = Assert.Single(Assert.Single(host.Applied).Placements);
        Assert.Equal(Vec3.Zero, placement.PositionM);
        Assert.Equal(new[] { "brief", "reference", "recipe-a1" }, agent.Requests.Select(r => r.Name));
        Assert.Contains(agent.Requests, r => r.Name == "reference" && r.Kind == StageKind.Image);
    }

    [Fact]
    public async Task Known_dimensions_are_enforced_and_confirmed()
    {
        var (session, agent, _) = Create();
        agent.Script["brief"] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(Samples.StoolBrief()) });
        var draft = StoolDraft(new Vec3(0.6, 0.5, 0.75));
        await session.GenerateAsync(draft, Settings);
        Assert.True(session.Brief!.Assets[0].DimensionsConfirmed);
        Assert.Equal(new Vec3(0.6, 0.5, 0.75), session.Brief.Assets[0].SizeM);
    }

    [Fact]
    public async Task A_rejected_recipe_is_corrected_with_a_patch_pinned_to_its_hash()
    {
        var (session, agent, _) = Create();
        var tooTall = new RecipeDraft("a1", Samples.StoolParts(height: 0.9));
        agent.Script["brief"] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(Samples.StoolBrief()) });
        agent.Script["recipe-a1"] = new(new Func<StageRequest, string>[]
        {
            _ => StudioJson.Write(tooTall),
            request =>
            {
                Assert.Contains("dimension_mismatch", request.Prompt);
                var fixedParts = Samples.StoolParts().Where(p => p.Name is "seat" or "leg-fl" or "leg-fr" or "leg-bl" or "leg-br").ToArray();
                return StudioJson.Write(new CandidatePatch<RecipeDraft>(StudioJson.Hash(tooTall), new RecipeDraft("a1", fixedParts)));
            }
        });
        var draft = StoolDraft();
        await session.GenerateAsync(draft, Settings);
        session.Accept(draft);
        await session.BuildAsync(Settings, reviewAfterBuild: false);
        Assert.Equal(StudioState.Built, session.State);
        Assert.Equal(2, agent.Requests.Count(r => r.Name == "recipe-a1"));
    }

    [Fact]
    public async Task Cancelling_reports_cancelled_and_keeps_the_brief()
    {
        var (session, agent, _) = Create();
        agent.Script["brief"] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(Samples.StoolBrief()) });
        var draft = StoolDraft();
        await session.GenerateAsync(draft, Settings);
        session.Accept(draft);
        agent.BeforeEachStage = async token => { session.Cancel(); await Task.Delay(Timeout.Infinite, token); };
        await session.BuildAsync(Settings, reviewAfterBuild: false);
        Assert.Equal(StudioState.Cancelled, session.State);
        Assert.NotNull(session.Brief);
        Assert.True(session.IsAccepted);
    }

    [Fact]
    public async Task Inputs_that_change_after_preparation_cannot_be_accepted()
    {
        var (session, agent, _) = Create();
        agent.Script["brief"] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(Samples.StoolBrief()) });
        await session.GenerateAsync(StoolDraft(), Settings);
        var edited = StoolDraft() with { Assets = new[] { "A three-legged stool" } };
        Assert.Throws<InvalidOperationException>(() => session.Accept(edited));
    }

    [Fact]
    public async Task Sizes_shown_to_a_tenth_of_a_millimetre_still_match_the_inputs()
    {
        var (session, agent, _) = Create();
        agent.Script["brief"] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(Samples.StoolBrief()) });
        await session.GenerateAsync(StoolDraft(new Vec3(25.5 * 0.0254, 20.2 * 0.0254, 30.3 * 0.0254)), Settings); // typed in inches
        Assert.Throws<InvalidOperationException>(() => session.Accept(StoolDraft(new Vec3(0.6487, 0.5131, 0.7696)))); // 1 mm wider
        session.Accept(StoolDraft(new Vec3(0.6477, 0.5131, 0.7696))); // as a reopened design shows it
        Assert.True(session.IsAccepted);
    }

    [Fact]
    public async Task Closing_the_preview_room_keeps_the_recipes_for_the_next_build()
    {
        var (session, agent, host) = Create();
        agent.Script["brief"] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(Samples.StoolBrief()) });
        agent.Script["recipe-a1"] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(new RecipeDraft("a1", Samples.StoolParts())) });
        var draft = StoolDraft();
        await session.GenerateAsync(draft, Settings);
        session.Accept(draft);
        await session.BuildAsync(Settings, reviewAfterBuild: false);

        var seen = new List<(StudioState State, bool HasFamilies)>();
        session.Changed += () => seen.Add((session.State, session.HasFamilies));
        host.CloseRoom();
        Assert.Equal(StudioState.DocumentUnavailable, session.State);
        Assert.False(session.HasFamilies);
        Assert.DoesNotContain(seen, s => s.State == StudioState.DocumentUnavailable && s.HasFamilies);
        session.Cancel(); // nothing is running: a no-op

        await session.BuildAsync(Settings, reviewAfterBuild: false);
        Assert.Equal(StudioState.Built, session.State);
        Assert.Equal(1, agent.Requests.Count(r => r.Name == "recipe-a1"));
        Assert.Equal(2, host.Applied.Count);
    }

    [Fact]
    public async Task An_instance_Revit_puts_elsewhere_fails_the_build()
    {
        var (session, agent, host) = Create();
        agent.Script["brief"] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(Samples.StoolBrief()) });
        agent.Script["recipe-a1"] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(new RecipeDraft("a1", Samples.StoolParts())) });
        var draft = StoolDraft();
        await session.GenerateAsync(draft, Settings);
        session.Accept(draft);
        host.Misplace = true;
        await session.BuildAsync(Settings, reviewAfterBuild: false);
        Assert.Equal(StudioState.Error, session.State);
        Assert.Contains("away from its planned position", session.Status);
    }

    [Fact]
    public async Task A_collection_is_planned_arranged_and_built()
    {
        var (session, agent, host) = Create();
        var brief = Samples.CollectionBrief();
        ScriptCollection(agent, brief);

        var draft = Presets.Create("office");
        await session.GenerateAsync(draft, Settings);
        Assert.Equal(StudioState.Review, session.State);
        session.Accept(draft);
        await session.BuildAsync(Settings, reviewAfterBuild: false);

        Assert.True(session.State == StudioState.Built, $"{session.State}: {session.Status}");
        var proposal = Assert.Single(host.Applied);
        Assert.Equal(7, proposal.Recipes.Length);
        Assert.Equal(brief.Assets.Select(a => a.Id), proposal.Recipes.Select(r => r.AssetId)); // brief order, whatever finished first
        Assert.Equal(0.75, proposal.Placements.Single(p => p.Key == "a2-1").PositionM.Z, 6);
    }

    [Fact]
    public async Task A_collection_plans_its_recipes_in_parallel()
    {
        var (session, agent, host) = Create();
        ScriptCollection(agent, Samples.CollectionBrief());
        agent.RecipeDelay = TimeSpan.FromMilliseconds(100);

        var draft = Presets.Create("office");
        await session.GenerateAsync(draft, Settings);
        session.Accept(draft);
        await session.BuildAsync(Settings, reviewAfterBuild: false);

        Assert.True(session.State == StudioState.Built, $"{session.State}: {session.Status}");
        Assert.InRange(agent.MaxRunning, 2, StudioSession.MaxParallelPlans);
        Assert.Equal(7, Assert.Single(host.Applied).Recipes.Length);
    }

    [Fact]
    public async Task Stage_progress_shows_on_the_detail_line()
    {
        var (session, agent, _) = Create();
        agent.Script["brief"] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(Samples.StoolBrief()) });
        agent.Script["recipe-a1"] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(new RecipeDraft("a1", Samples.StoolParts())) });
        string? seen = null;
        agent.DuringStage = request =>
        {
            if (request.Name != "recipe-a1") return;
            agent.Report(new StageProgress("recipe-a1", "s2", StagePhase.Writing, TimeSpan.FromSeconds(250), 9000, 23));
            seen = session.Detail;
            agent.Report(new StageProgress("recipe-a1", "s2", StagePhase.Finished, TimeSpan.FromSeconds(251), 9100, 24));
        };

        var draft = StoolDraft();
        await session.GenerateAsync(draft, Settings);
        session.Accept(draft);
        await session.BuildAsync(Settings, reviewAfterBuild: false);

        Assert.Equal("Writing, 23 parts so far (4:10)", seen);
        Assert.Equal("shape", agent.Requests.Single(r => r.Name == "recipe-a1").CountKey);
        Assert.Equal(StudioState.Built, session.State);
    }

    private static void ScriptCollection(ScriptedAgent agent, StudioBrief brief)
    {
        agent.Script["brief"] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(brief) });
        agent.Script["recipe-a1"] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(new RecipeDraft("a1", Samples.Table(brief).Parts)) });
        for (var i = 2; i <= 7; i++)
        {
            var id = $"a{i}";
            var floor = i != 2;
            agent.Script["recipe-" + id] = new(new Func<StageRequest, string>[] { _ => StudioJson.Write(new RecipeDraft(id, Samples.Box(id, new Vec3(0.5, 0.5, 0.5), brief, floor).Parts)) });
        }
        agent.Script["layout"] = new(new Func<StageRequest, string>[]
        {
            _ => StudioJson.Write(new PlacementIntentPlan(new[]
            {
                new PlacementIntent("a1-1", "a1", "absolute", Vec3.Zero, 0, null, null, null, null, null, null),
                new PlacementIntent("a2-1", "a2", "surface", Vec3.Zero, 0, "a1-1", "top", null, null, null, null)
            }.Concat(Enumerable.Range(3, 5).Select(i => new PlacementIntent($"a{i}-1", $"a{i}", "absolute", new Vec3(-3.2 + (i - 3) * 1.2, -2.2, 0), 0, null, null, null, null, null, null))).ToArray()))
        });
    }
}
