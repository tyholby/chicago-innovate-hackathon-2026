using FamilyStudio.Core.Json;
using FamilyStudio.Core.Model;
using FamilyStudio.Core.Prompts;
using FamilyStudio.Core.Validation;

namespace FamilyStudio.Core.Pipeline;

public enum StudioState
{
    Draft,
    PreparingBrief,
    GeneratingReference,
    Review,
    Building,
    Checking,
    Repairing,
    Ready,
    Built,
    LimitReached,
    SignInRequired,
    TimedOut,
    Cancelled,
    DocumentUnavailable,
    Error,
    Closed
}

/// <summary>The model and detail level an operation runs with.</summary>
public sealed record StageSettings(ModelChoice Model, Fidelity Fidelity);

/// <summary>One line in the session's activity log, shown like a drawing issue log.</summary>
public sealed record ActivityEntry(DateTimeOffset Time, string Text, TimeSpan? Duration, bool IsError);

/// <summary>
/// Runs one design from brief to native families: prepare the brief, make or import the reference,
/// accept, plan each family, arrange a collection, build natively, and optionally review and repair.
/// One operation runs at a time; each can be cancelled, and completed native work always survives.
/// Within a build, a collection plans its recipes in parallel.
/// </summary>
public sealed class StudioSession : IDisposable
{
    /// <summary>Recipes a collection plans at once, each in its own Codex thread.</summary>
    public const int MaxParallelPlans = Codex.CodexService.MaxConcurrentStages;

    private static readonly TimeSpan BriefTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReferenceTimeout = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan PlanningTimeout = TimeSpan.FromMinutes(20); // detailed shapes at xhigh effort take longer
    private static readonly TimeSpan ReviewTimeout = TimeSpan.FromMinutes(10);

    private readonly IStudioAgent _agent;
    private readonly IStudioHost _host;
    private readonly List<ActivityEntry> _activity = new();
    private readonly Dictionary<string, FamilyRecipe> _planned = new(StringComparer.Ordinal); // written under its own lock while planning in parallel
    private readonly Dictionary<string, StageProgress> _live = new(StringComparer.Ordinal); // running stages by ID; also guards _stageLabels
    private readonly Dictionary<string, string> _stageLabels = new(StringComparer.Ordinal); // stage name to item name, for the detail line
    private CancellationTokenSource? _operation;
    private Task _running = Task.CompletedTask;
    private StudioDraft? _generatedFrom;
    private AcceptedDesign? _accepted;
    private BuildProposal? _built;
    private PlacementIntentPlan? _intents;
    private ModelChoice? _plannedWith;
    private Fidelity _fidelity = Fidelity.Concept;
    private bool _disposed;

    public SessionJournal Journal { get; }
    public StudioState State { get; private set; } = StudioState.Draft;
    public string Status { get; private set; } = "Describe an item or add a photo to begin.";
    public string Detail { get; private set; } = "";
    public StudioBrief? Brief { get; private set; }
    public ReferenceImage? Reference { get; private set; }
    public NativeSnapshot? Snapshot { get; private set; }
    public ReviewReport? LastReview { get; private set; }
    public bool IsBusy => Volatile.Read(ref _operation) is not null;
    public bool IsAccepted => _accepted is not null;
    public bool HasFamilies => Snapshot?.Families.Length > 0;
    public IReadOnlyList<ActivityEntry> Activity { get { lock (_activity) return _activity.ToArray(); } }
    public Task WhenSettled => Task.WhenAll(_running, _host.WhenIdle);

    /// <summary>Raised whenever state, status or results change. May fire on any thread.</summary>
    public event Action? Changed;

    public StudioSession(IStudioAgent agent, IStudioHost host, SessionJournal journal)
    {
        _agent = agent;
        _host = host;
        Journal = journal;
        _agent.Progress += OnProgress;
        _host.DocumentUnavailable += OnDocumentUnavailable;
    }

    // ---- 1. brief and reference -----------------------------------------------------------

    public Task GenerateAsync(StudioDraft draft, StageSettings settings) => RunOperation("Prepare design", async token =>
    {
        draft.Validate();
        ClearDesign();
        _fidelity = settings.Fidelity;
        _generatedFrom = Copy(draft);
        Journal.Artifact($"draft-{DateTime.Now:HHmmss}.json", StudioJson.Write(draft));

        var upload = draft.ReferenceImagePath is null ? null : ReferenceImages.ImportUpload(draft.ReferenceImagePath, Journal);
        SetState(StudioState.PreparingBrief, draft.IsSingleItem ? "Reading your item and its dimensions..." : "Reading your collection and its dimensions...");
        var result = await Timed("Brief prepared", () => _agent.RunAsync(new StageRequest("brief", StageKind.Reasoning, settings.Model,
            StudioPrompts.Developer(false), StudioPrompts.Brief(draft, settings.Fidelity),
            upload is null ? Array.Empty<string>() : new[] { upload.Path }, OutputSchemas.Brief(), BriefTimeout), token));

        var brief = draft.ApplyNames(StudioJson.Read<StudioBrief>(result.Text));
        brief = brief with { Assets = brief.Assets.Select(a => a with { DimensionsConfirmed = false }).ToArray() };
        brief.Validate();
        if (brief.Assets.Length != draft.Assets.Length)
            throw new StudioProtocolException("The brief does not have the same number of items as your request.");
        if (draft.IsSingleItem)
        {
            var asset = brief.Assets[0];
            if (!asset.FloorStanding)
                throw new ArgumentException("A single item must stand on the floor. Describe a freestanding piece of furniture.");
            var size = draft.KnownSizeM ?? asset.SizeM;
            Dimensions.Validate(size);
            brief = brief with { Assets = new[] { asset with { Quantity = 1, SizeM = size, DimensionsConfirmed = draft.KnownSizeM is not null } } };
        }
        brief.Validate();
        Brief = brief;
        Journal.Artifact("brief.json", StudioJson.Write(brief));

        if (upload is not null) Reference = upload;
        else
        {
            SetState(StudioState.GeneratingReference, brief.IsSingleItem ? "Drawing a reference image of the item..." : "Drawing the collection's reference sheet...");
            var image = await Timed("Reference drawn", () => _agent.RunAsync(new StageRequest("reference", StageKind.Image, settings.Model,
                StudioPrompts.Developer(true), StudioPrompts.Reference(brief), Array.Empty<string>(), null, ReferenceTimeout), token));
            Reference = ReferenceImages.ImportGenerated(image, Journal);
        }
        SetState(StudioState.Review, brief.IsSingleItem
            ? "Check the name, dimensions and finishes, then accept the design."
            : "Check the reference sheet and dimensions, then accept the design.");
    });

    /// <summary>Applies the user's edits to a single item under review, before acceptance.</summary>
    public void UpdateSingleItem(string name, string description, Vec3 size, bool confirmed, MaterialBrief[] materials)
    {
        if (_disposed || IsBusy || Brief is not { IsSingleItem: true } || Reference is null || _accepted is not null)
            throw new InvalidOperationException("Edit the item while it is under review, before accepting it.");
        Dimensions.Validate(size);
        var asset = Brief.Assets[0] with { Name = name.Trim(), Description = description.Trim(), SizeM = size, DimensionsConfirmed = confirmed };
        var brief = Brief with { Assets = new[] { asset }, Materials = materials };
        brief.Validate();
        Brief = brief;
        Journal.Artifact("brief-reviewed.json", StudioJson.Write(brief));
        Changed?.Invoke();
    }

    public void Accept(StudioDraft currentDraft)
    {
        if (IsBusy || State != StudioState.Review || Reference is null || Brief is null)
            throw new InvalidOperationException("Prepare and review a design before accepting it.");
        if (_generatedFrom is null || !currentDraft.SameInputs(_generatedFrom))
            throw new InvalidOperationException("The inputs changed after this reference was made. Prepare the design again to use them.");
        _accepted = new AcceptedDesign(Brief, Reference);
        _host.BindDesign(_accepted.Brief);
        ResetBuild();
        var path = AcceptedDesignFile.Save(Journal, _accepted, currentDraft);
        Journal.Write("design_accepted", new { _accepted.BriefSha256, reference = Reference.Sha256, path });
        Log("Design accepted");
        Status = Brief.IsSingleItem ? "Design accepted. Build the family when you are ready." : "Design accepted. Build the families when you are ready.";
        Changed?.Invoke();
    }

    /// <summary>Reopens a saved design. Returns the draft that produced it, so the inputs can be shown again.</summary>
    public StudioDraft OpenAccepted(string path)
    {
        if (IsBusy || _disposed) throw new InvalidOperationException("Finish the current step before opening a design.");
        var file = AcceptedDesignFile.Load(path);
        ClearDesign();
        var image = Journal.PathFor(System.IO.Path.GetFileName(file.Reference.Path));
        if (!File.Exists(image)) File.Copy(file.Reference.Path, image);
        Reference = file.Reference with { Path = image };
        Brief = file.Brief;
        _accepted = new AcceptedDesign(file.Brief, Reference);
        var draft = file.Draft with { ReferenceImagePath = Reference.IsUpload && file.Draft.IsSingleItem ? image : null };
        _generatedFrom = Copy(draft);
        _host.BindDesign(_accepted.Brief);
        Journal.Write("design_reopened", new { source = path, _accepted.BriefSha256 });
        Log("Accepted design reopened");
        SetState(StudioState.Review, "Accepted design reopened. Build it into a new preview room.");
        return draft;
    }

    /// <summary>Returns a built single item to review, so its dimensions and finishes can change. Outputs on disk are kept.</summary>
    public void ReopenForEdits()
    {
        if (IsBusy || Brief is not { IsSingleItem: true } || Reference is null)
            throw new InvalidOperationException("Open a single item before editing it.");
        _accepted = null;
        ResetBuild();
        SetState(StudioState.Review, "Edit the dimensions and finishes, then accept and build again. Earlier files are kept.");
    }

    public void StartNew(StudioDraft current, string presetId)
    {
        if (_disposed || IsBusy) throw new InvalidOperationException("Finish the current step before starting a new design.");
        if (current.HasContent) Journal.Artifact($"draft-archived-{DateTime.Now:HHmmss}.json", StudioJson.Write(current));
        ClearDesign();
        Journal.Write("design_started", new { presetId });
        SetState(StudioState.Draft, presetId == Presets.SingleId
            ? "Describe an item or add a photo to begin."
            : "Edit the items and materials, then prepare the reference sheet.");
    }

    /// <summary>Called when the inputs change after a reference was prepared.</summary>
    public void InvalidateDraft()
    {
        if (IsBusy || _disposed || State == StudioState.Draft || _accepted is not null) return;
        ClearDesign();
        SetState(StudioState.Draft, "Inputs changed. Prepare the design again to use them.");
    }

    // ---- 2. build ---------------------------------------------------------------------------

    public Task BuildAsync(StageSettings settings, bool reviewAfterBuild) => RunOperation("Build", async token =>
    {
        var design = _accepted ?? throw new InvalidOperationException("Accept the design before building.");
        design.Verify();
        var brief = design.Brief;
        if (_fidelity != settings.Fidelity || _plannedWith != settings.Model) { _planned.Clear(); _intents = null; _fidelity = settings.Fidelity; }
        _plannedWith = settings.Model;

        SetState(StudioState.Building, "Opening the preview room in Revit...");
        Snapshot = await _host.EnsureRoomAsync(token);
        if (Snapshot.Families.Length == 0)
        {
            // Resuming after a cancelled or failed build plans only the items still missing.
            await PlanRecipesAsync(brief, brief.Assets.Where(a => !_planned.ContainsKey(a.Id)).ToArray(), design.Reference, settings, token);

            var recipes = brief.Assets.Select(a => _planned[a.Id]).ToArray();
            if (IsSingleFreestanding(brief))
            {
                var bounds = RecipeRules.Bounds(recipes[0].Parts);
                var centred = new Placement($"{brief.Assets[0].Id}-1", brief.Assets[0].Id,
                    new Vec3(-(bounds.Min.X + bounds.Max.X) / 2, -(bounds.Min.Y + bounds.Max.Y) / 2, 0), 0, null, null);
                _intents = new(new[] { PlacementIntent.AbsoluteFrom(centred) });
                SetState(StudioState.Building, "Building the family in Revit...");
                await Timed("Family built in Revit", () => ApplyAsync(new BuildProposal(recipes, new[] { centred }), brief, token));
            }
            else
            {
                // A layout survives a closed preview room, like the recipes it was planned for.
                var plan = _intents;
                if (plan is null)
                {
                    SetState(StudioState.Building, "Arranging the collection in the preview room...");
                    plan = await Timed("Layout validated", () => PlanLayoutAsync(brief, recipes, design.Reference, settings, token));
                    _intents = plan;
                }
                SetState(StudioState.Building, "Building the families in Revit...");
                await Timed("Families built in Revit", () => ApplyAsync(new BuildProposal(recipes, new PlacementResolver(plan, recipes).Resolve()), brief, token));
            }
        }

        if (reviewAfterBuild) await ReviewAndRepairAsync(design, settings, token);
        else
        {
            Snapshot = await _host.CaptureAsync(token);
            LastReview = null;
            SetState(StudioState.Built, brief.IsSingleItem ? "Built. Inspect the views, then save or load the family." : "Built. Inspect the views, then load the families.",
                "AI review has not run. Choose Review to check the build against the reference.");
        }
    });

    public Task ReviewAsync(StageSettings settings) => RunOperation("Review", token =>
    {
        var design = _accepted ?? throw new InvalidOperationException("Accept and build the design before reviewing it.");
        if (!HasFamilies) throw new InvalidOperationException("Build the design before reviewing it.");
        return ReviewAndRepairAsync(design, settings, token);
    });

    /// <summary>Applies a shape change described in words to a built single item.</summary>
    public Task RefineAsync(string request, StageSettings settings) => RunOperation("Refine", async token =>
    {
        var design = _accepted ?? throw new InvalidOperationException("Accept and build the item before refining it.");
        if (string.IsNullOrWhiteSpace(request)) throw new ArgumentException("Describe the change you want.");
        if (!design.Brief.IsSingleItem || _built is null || Snapshot?.Families.Length != 1)
            throw new InvalidOperationException("Build a single item before refining it.");
        design.Verify();
        var brief = design.Brief;
        var asset = brief.Assets[0];
        var current = _built.Recipes.Single();
        SetState(StudioState.Building, "Revising the geometry...");
        var draft = await Timed("Revision validated", () => CandidateLoop.RunAsync<RecipeDraft>(_agent, Journal,
            new StageRequest("refine-" + asset.Id, StageKind.Reasoning, settings.Model, StudioPrompts.Developer(false),
                StudioPrompts.Refine(brief, asset, new RecipeDraft(current.AssetId, current.Parts), request, settings.Fidelity),
                new[] { design.Reference.Path }, OutputSchemas.Recipe(brief, asset), PlanningTimeout, CountKey: "shape"),
            candidate => CheckRecipe(candidate, asset, brief), token,
            OutputSchemas.RecipePatch(brief, asset), RecipeDraft.Merge));
        var recipe = draft.Compile(brief);
        _planned[asset.Id] = recipe;
        SetState(StudioState.Building, "Rebuilding the family in Revit...");
        await Timed("Family rebuilt in Revit", () => ApplyAsync(new BuildProposal(new[] { recipe }, Array.Empty<Placement>()), brief, token));
        Snapshot = await _host.CaptureAsync(token);
        LastReview = null;
        SetState(StudioState.Built, "Revised. Inspect the views, then save or load the family.");
    });

    // ---- 3. deliver ------------------------------------------------------------------------

    public Task<ProjectChoice[]> ListProjectsAsync(CancellationToken cancellationToken) => _host.ListProjectsAsync(cancellationToken);

    public Task LoadAsync(string projectKey, IReadOnlyList<string> assetIds) => RunOperation("Load", async token =>
    {
        var design = _accepted ?? throw new InvalidOperationException("Build the design before loading it.");
        if (!HasFamilies) throw new InvalidOperationException("Build the design before loading it.");
        design.Verify();
        await _host.LoadFamiliesAsync(projectKey, assetIds, token);
        Log(assetIds.Count == 1 ? "Family loaded into the project" : $"{assetIds.Count} families loaded into the project");
        Status = "Loaded. Place it with Revit's Component tool.";
        Changed?.Invoke();
    }, keepState: true);

    public Task ShowRoomAsync() => _host.ShowRoomAsync(CancellationToken.None);

    /// <summary>Cancels the running operation, if any. Safe from any thread, even as the operation ends.</summary>
    public void Cancel()
    {
        try { Volatile.Read(ref _operation)?.Cancel(); }
        catch (ObjectDisposedException) { /* the operation finished meanwhile */ }
    }

    // ---- planning helpers -------------------------------------------------------------------

    /// <summary>
    /// Plans items' recipes, up to <see cref="MaxParallelPlans"/> at once: a collection then takes about as
    /// long as its slowest items, not all seven in a row. Each recipe is kept the moment it validates, so a
    /// failure or cancel loses only the items still in progress, and building again resumes from there.
    /// </summary>
    private async Task PlanRecipesAsync(StudioBrief brief, AssetBrief[] assets, ReferenceImage reference, StageSettings settings, CancellationToken token)
    {
        if (assets.Length == 0) return;
        var ready = brief.Assets.Length - assets.Length;
        void Announce() => SetState(StudioState.Building, brief.IsSingleItem
            ? $"Designing the geometry of {assets[0].Name}..."
            : assets.Length == 1
                ? $"Designing {assets[0].Name}..."
                : $"Designing {assets.Length} families, up to {MaxParallelPlans} at once: {Volatile.Read(ref ready)} of {brief.Assets.Length} ready...");
        Announce();
        using var slots = new SemaphoreSlim(MaxParallelPlans);
        await Task.WhenAll(assets.Select(async asset =>
        {
            await slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var recipe = await Timed($"{asset.Name}: geometry validated", () => PlanRecipeAsync(brief, asset, reference, settings, token)).ConfigureAwait(false);
                lock (_planned) _planned[asset.Id] = recipe;
                Interlocked.Increment(ref ready);
                if (assets.Length > 1) Announce();
            }
            finally { slots.Release(); }
        })).ConfigureAwait(false);
    }

    private async Task<FamilyRecipe> PlanRecipeAsync(StudioBrief brief, AssetBrief asset, ReferenceImage reference, StageSettings settings, CancellationToken token)
    {
        var stage = "recipe-" + asset.Id;
        lock (_live) _stageLabels[stage] = asset.Name;
        var draft = await CandidateLoop.RunAsync<RecipeDraft>(_agent, Journal,
            new StageRequest(stage, StageKind.Reasoning, settings.Model, StudioPrompts.Developer(false),
                StudioPrompts.Recipe(brief, asset, settings.Fidelity), new[] { reference.Path },
                OutputSchemas.Recipe(brief, asset), PlanningTimeout, CountKey: "shape"),
            candidate => CheckRecipe(candidate, asset, brief), token,
            OutputSchemas.RecipePatch(brief, asset), RecipeDraft.Merge).ConfigureAwait(false);
        return draft.Compile(brief);
    }

    private static RecipeDraft CheckRecipe(RecipeDraft candidate, AssetBrief asset, StudioBrief brief)
    {
        if (candidate.AssetId != asset.Id)
            throw StudioValidationException.Single("wrong_asset", "assetId", $"Return the recipe for {asset.Id} only.", asset.Id);
        candidate.Compile(brief);
        return candidate;
    }

    private Task<PlacementIntentPlan> PlanLayoutAsync(StudioBrief brief, FamilyRecipe[] recipes, ReferenceImage reference, StageSettings settings, CancellationToken token) =>
        CandidateLoop.RunAsync<PlacementIntentPlan>(_agent, Journal,
            new StageRequest("layout", StageKind.Reasoning, settings.Model, StudioPrompts.Developer(false),
                StudioPrompts.Layout(brief, recipes), new[] { reference.Path }, OutputSchemas.Layout(brief), PlanningTimeout, CountKey: "key"),
            candidate =>
            {
                var placements = new PlacementResolver(candidate, recipes).Resolve();
                PlacementRules.Validate(new BuildProposal(recipes, placements), brief);
                return candidate;
            }, token, OutputSchemas.LayoutPatch(brief), PlacementIntentPlan.Merge);

    private async Task ApplyAsync(BuildProposal proposal, StudioBrief brief, CancellationToken token)
    {
        var complete = BuildProposal.Merge(_built, proposal);
        var overlaps = PlacementRules.Validate(complete, brief);
        var expected = Snapshot is null ? null : new NativeExpectation(Snapshot.DocumentKey, Snapshot.ChangeStamp);
        token.ThrowIfCancellationRequested();
        Snapshot = await _host.ApplyAsync(proposal, expected, token);
        _built = complete;
        foreach (var asset in brief.Assets)
            if (!Snapshot.Families.Any(f => f.AssetId == asset.Id) || Snapshot.Instances.Count(i => i.AssetId == asset.Id) != asset.Quantity)
                throw new InvalidOperationException($"Revit's model is missing {asset.Name}. Build again to continue.");
        // Measure the placements back, like the families: Revit must have put every instance where it was planned.
        foreach (var placement in complete.Placements)
        {
            var instance = Snapshot.Instances.SingleOrDefault(i => i.Key == placement.Key);
            if (instance is null || !SamePlace(instance, placement))
                throw new InvalidOperationException($"Revit placed {brief.Assets.Single(a => a.Id == placement.AssetId).Name} away from its planned position. Build again to continue.");
        }
        Journal.Write("native_build", new { Snapshot.DocumentKey, families = Snapshot.Families.Length, instances = Snapshot.Instances.Length, overlaps });
        if (_intents is not null) Journal.Artifact("placement-intents.json", StudioJson.Write(_intents));
        Changed?.Invoke();
    }

    private async Task ReviewAndRepairAsync(AcceptedDesign design, StageSettings settings, CancellationToken token)
    {
        design.Verify();
        var brief = design.Brief;
        Snapshot = await _host.CaptureAsync(token);
        for (var pass = 0; pass <= 2; pass++)
        {
            SetState(StudioState.Checking, pass == 0 ? "Comparing the Revit views with the reference..." : $"Checking repair {pass} of 2...");
            try { LastReview = await Timed(pass == 0 ? "Review finished" : $"Repair {pass} reviewed", () => RunReviewAsync(brief, design.Reference, settings, token)); }
            catch (Exception error) when (error is TimeoutException or StudioLimitException or IOException)
            {
                token.ThrowIfCancellationRequested();
                LastReview = null;
                Journal.Write("review_unfinished", new { pass, error.Message });
                SetState(StudioState.Built, "Built. The AI review did not finish, but the families are ready to use.", error.Message);
                return;
            }
            if (LastReview.Passed)
            {
                SetState(StudioState.Ready, brief.IsSingleItem ? "Ready. The family matches its reference." : "Ready. The families match their reference.", LastReview.Summary);
                return;
            }
            if (pass == 2) break;
            SetState(StudioState.Repairing, $"Repairing the findings, pass {pass + 1} of 2...");
            await Timed($"Repair {pass + 1} applied", () => RepairAsync(brief, design.Reference, LastReview, settings, token));
            Snapshot = await _host.CaptureAsync(token);
        }
        SetState(StudioState.Built, "Two repair passes are done. Review the remaining findings.", LastReview?.Summary ?? "");
    }

    private async Task<ReviewReport> RunReviewAsync(StudioBrief brief, ReferenceImage reference, StageSettings settings, CancellationToken token)
    {
        var snapshot = Snapshot ?? throw new InvalidOperationException("Capture the preview room before reviewing it.");
        var views = NativeSnapshot.CaptureViews.Select(key => snapshot.Captures.SingleOrDefault(c => c.ViewKey == key)
            ?? throw new StudioEvidenceException("The review needs the plan and both 3D views.")).ToArray();
        var evidence = new { reference = reference.Sha256, views = views.Select(v => new { v.ViewKey, v.Width, v.Height, v.Camera }) };
        var result = await _agent.RunAsync(new StageRequest("review", StageKind.Reasoning, settings.Model, StudioPrompts.Developer(false),
            StudioPrompts.Review(brief, snapshot, settings.Fidelity, evidence),
            new[] { reference.Path }.Concat(views.Select(v => v.Path)).ToArray(), OutputSchemas.Review(brief), ReviewTimeout), token);
        var review = StudioJson.Read<ReviewReport>(result.Text);
        review.Validate(brief);
        if (review.Findings.Any(f => f.PlacementKey is not null && !snapshot.Instances.Any(i => i.Key == f.PlacementKey && i.AssetId == f.AssetId)))
            throw new StudioProtocolException("The review named an instance that is not in the preview room.");
        var current = await _host.EnsureRoomAsync(token);
        if (current.ChangeStamp != snapshot.ChangeStamp)
            throw new StudioEvidenceException("The preview room changed during the review. Review it again.");
        Journal.Artifact($"review-{result.StageId}.json", StudioJson.Write(review));
        return review;
    }

    private async Task RepairAsync(StudioBrief brief, ReferenceImage reference, ReviewReport review, StageSettings settings, CancellationToken token)
    {
        var before = _built ?? throw new InvalidOperationException("A repair needs the built scene.");
        var intents = _intents ?? PlacementRules.ToIntents(before);
        var snapshot = Snapshot!;
        var baseHash = StudioJson.Hash(new { before, intents, snapshot.DocumentKey, snapshot.ChangeStamp });
        (FamilyRecipe[] Recipes, PlacementIntentPlan Intents) Merge(SceneRepair repair)
        {
            if (repair.BaseSha256 != baseHash)
                throw StudioValidationException.Single("stale_patch", "baseSha256", "Use the exact base hash supplied.");
            if (repair.Recipes is null || repair.Placements is null || repair.Recipes.Any(e => e?.UpsertParts is null || e.RemoveParts is null))
                throw StudioValidationException.Single("incomplete_repair", "recipes/placements", "Every list in a repair must be present, even when empty.");
            if (repair.Recipes.Length == 0 && repair.Placements.Length == 0)
                throw StudioValidationException.Single("empty_repair", "recipes/placements", "A repair must change something the findings mention.");
            var recipes = before.Recipes.ToDictionary(r => r.AssetId, StringComparer.Ordinal);
            foreach (var edit in repair.Recipes)
            {
                if (!recipes.TryGetValue(edit.AssetId, out var recipe) ||
                    edit.RemoveParts.Any(n => !recipe.Parts.Any(p => p.Name == n)) || edit.UpsertParts.Any(p => edit.RemoveParts.Contains(p.Name)))
                    throw StudioValidationException.Single("invalid_edit", "recipes", "Edits must name existing items, remove existing parts, and not upsert a removed part.", edit.AssetId);
                var parts = Named.Merge(recipe.Parts.Where(p => !edit.RemoveParts.Contains(p.Name)).ToArray(), edit.UpsertParts, p => p.Name);
                recipes[edit.AssetId] = new RecipeDraft(edit.AssetId, parts).Compile(brief);
            }
            if (repair.Placements.Any(p => !intents.Placements.Any(i => i.Key == p.Key && i.AssetId == p.AssetId)))
                throw StudioValidationException.Single("unknown_placement", "placements", "Repairs keep the existing placement keys and items.");
            var merged = PlacementIntentPlan.Merge(intents, new PlacementIntentPlan(repair.Placements));
            return (recipes.Values.ToArray(), merged);
        }

        var accepted = await CandidateLoop.RunAsync<SceneRepair>(_agent, Journal,
            new StageRequest("repair", StageKind.Reasoning, settings.Model, StudioPrompts.Developer(false),
                StudioPrompts.Repair(brief, snapshot, review, before, intents, baseHash, settings.Fidelity),
                new[] { reference.Path }, OutputSchemas.Repair(brief), PlanningTimeout, CountKey: "shape"),
            candidate =>
            {
                var (recipes, merged) = Merge(candidate);
                if (!IsSingleFreestanding(brief))
                    PlacementRules.Validate(new BuildProposal(recipes, new PlacementResolver(merged, recipes).Resolve()), brief);
                return candidate;
            }, token);

        var (repairedRecipes, repairedIntents) = Merge(accepted);
        var placements = IsSingleFreestanding(brief) ? before.Placements : new PlacementResolver(repairedIntents, repairedRecipes).Resolve();
        var changed = repairedRecipes.Where(r => accepted.Recipes.Any(e => e.AssetId == r.AssetId)).ToArray();
        var previous = _intents;
        _intents = repairedIntents;
        try { await ApplyAsync(new BuildProposal(changed, placements), brief, token); }
        catch { _intents = previous; throw; }
    }

    private static bool IsSingleFreestanding(StudioBrief brief) =>
        brief.Assets is [{ Quantity: 1, FloorStanding: true }];

    private static bool SamePlace(InstanceReceipt instance, Placement placement)
    {
        var offset = instance.PositionM - placement.PositionM;
        var turn = Math.Abs(instance.RotationDegrees - placement.RotationDegrees) % 360;
        return Math.Abs(offset.X) <= RecipeRules.ContainmentToleranceM && Math.Abs(offset.Y) <= RecipeRules.ContainmentToleranceM &&
               Math.Abs(offset.Z) <= RecipeRules.ContainmentToleranceM && Math.Min(turn, 360 - turn) <= 0.1;
    }

    // ---- operation plumbing ---------------------------------------------------------------

    private Task RunOperation(string name, Func<CancellationToken, Task> action, bool keepState = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBusy) throw new InvalidOperationException("Family Studio is already working. Wait for it to finish, or cancel.");
        var operation = new CancellationTokenSource();
        Volatile.Write(ref _operation, operation);
        _running = RunCoreAsync(name, action, operation, keepState);
        return _running;
    }

    private async Task RunCoreAsync(string name, Func<CancellationToken, Task> action, CancellationTokenSource operation, bool keepState)
    {
        var codex = _agent as Codex.CodexService;
        if (codex is not null) codex.Journal = Journal;
        Changed?.Invoke();
        try
        {
            await action(operation.Token).ConfigureAwait(false);
        }
        catch (StudioLimitException ex)
        {
            Fail(StudioState.LimitReached, ex.ResetsAt is DateTimeOffset reset
                ? $"Your ChatGPT usage limit was reached. It resets {reset.ToLocalTime():g}."
                : "Your ChatGPT usage limit was reached. Try again later.", ex.Message);
        }
        catch (StudioSignInRequiredException ex) { Fail(StudioState.SignInRequired, ex.Message, ""); }
        catch (StudioDocumentException ex) { Fail(StudioState.DocumentUnavailable, ex.Message, ""); }
        catch (StudioEvidenceException ex) { Fail(State is StudioState.Review ? StudioState.Review : StudioState.Built, ex.Message, ""); }
        catch (TimeoutException ex) { Fail(StudioState.TimedOut, ex.Message, "Completed work is kept. Try the step again."); }
        catch (OperationCanceledException)
        {
            if (State != StudioState.DocumentUnavailable)
                Fail(StudioState.Cancelled, "Cancelled. Completed work is kept.", "");
        }
        catch (Exception ex)
        {
            Journal.Write("operation_failed", new { name, type = ex.GetType().Name, ex.Message });
            Fail(keepState ? State : StudioState.Error, ex.Message, "");
        }
        finally
        {
            Volatile.Write(ref _operation, null);
            operation.Dispose();
            Changed?.Invoke();
        }
    }

    private async Task<T> Timed<T>(string label, Func<Task<T>> step)
    {
        var start = DateTimeOffset.Now;
        var result = await step().ConfigureAwait(false);
        Log(label, DateTimeOffset.Now - start);
        return result;
    }

    private async Task Timed(string label, Func<Task> step)
    {
        var start = DateTimeOffset.Now;
        await step().ConfigureAwait(false);
        Log(label, DateTimeOffset.Now - start);
    }

    private void Log(string text, TimeSpan? duration = null, bool error = false)
    {
        lock (_activity)
        {
            _activity.Add(new ActivityEntry(DateTimeOffset.Now, text, duration, error));
            if (_activity.Count > 200) _activity.RemoveAt(0);
        }
    }

    private void Fail(StudioState state, string status, string detail)
    {
        Log(status, error: true);
        SetState(state, status, detail);
    }

    private void SetState(StudioState state, string status, string detail = "")
    {
        if (_disposed) return;
        State = state;
        Status = status;
        Detail = detail;
        Journal.Write("state", new { state = state.ToString(), status, detail });
        Changed?.Invoke();
    }

    private void ClearDesign()
    {
        _accepted = null;
        _generatedFrom = null;
        Brief = null;
        Reference = null;
        ResetBuild();
    }

    private void ResetBuild()
    {
        _built = null;
        _intents = null;
        _planned.Clear();
        _plannedWith = null;
        Snapshot = null;
        LastReview = null;
    }

    private static StudioDraft Copy(StudioDraft d) =>
        new(d.Style, d.Assets.ToArray(), d.Materials.ToArray(), d.AssetNames.ToArray(), d.ReferenceImagePath, d.KnownSizeM);

    /// <summary>Shows what the running stages are doing, live, on the detail line.</summary>
    private void OnProgress(StageProgress progress)
    {
        string detail;
        lock (_live)
        {
            if (progress.Phase == StagePhase.Finished)
            {
                // The next state change sets its own detail; until then keep showing any other running stages.
                if (!_live.Remove(progress.StageId) || _live.Count == 0) return;
            }
            else _live[progress.StageId] = progress;
            detail = StageProgressText.Describe(_live.Values.OrderBy(p => p.StageName, StringComparer.Ordinal).ToArray(),
                name => _stageLabels.TryGetValue(name, out var label) ? label : null);
        }
        Detail = detail;
        Changed?.Invoke();
    }

    private void OnDocumentUnavailable()
    {
        if (_accepted is null) return;
        // Forget the room before announcing it, so listeners never see families that are gone.
        // Validated recipes and the layout stay: building again needs no new planning.
        if (_built is not null)
            foreach (var recipe in _built.Recipes) _planned[recipe.AssetId] = recipe;
        _built = null;
        Snapshot = null;
        LastReview = null;
        _host.BindDesign(_accepted.Brief);
        SetState(StudioState.DocumentUnavailable, "The preview room was closed. Build again to open a new one.");
        Cancel();
    }

    /// <summary>Cancels, waits for native work to settle, then closes the room binding.</summary>
    public async Task StopAsync()
    {
        Cancel();
        try { await WhenSettled.ConfigureAwait(false); }
        catch (Exception) { /* already reported */ }
        await _host.CloseAsync().ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        _agent.Progress -= OnProgress;
        _host.DocumentUnavailable -= OnDocumentUnavailable;
        State = StudioState.Closed;
    }
}
