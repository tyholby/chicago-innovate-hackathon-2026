using FamilyStudio.Core.Model;
using FamilyStudio.Core.Pipeline;
using FamilyStudio.Core.Validation;

namespace FamilyStudio.Probe;

/// <summary>
/// Stands in for Revit: "builds" families by measuring their recipes and places instances exactly
/// where the pipeline resolved them. Captures are SVG drawings rather than Revit exports.
/// </summary>
internal sealed class InMemoryHost(SessionJournal journal) : IStudioHost
{
    private readonly Dictionary<string, FamilyReceipt> _families = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Placement Placement, FamilyRecipe Recipe)> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FamilyRecipe> _recipes = new(StringComparer.Ordinal);
    private StudioBrief? _brief;
    private string _roomKey = Guid.NewGuid().ToString("N");
    private long _stamp;
    private int _captures;
    private CaptureReceipt[] _lastCaptures = Array.Empty<CaptureReceipt>();

    public event Action? DocumentUnavailable { add { } remove { } }
    public Task WhenIdle => Task.CompletedTask;

    public IReadOnlyDictionary<string, FamilyRecipe> Recipes => _recipes;
    public IEnumerable<(Placement Placement, FamilyRecipe Recipe)> Instances => _instances.Values;

    public void BindDesign(StudioBrief brief)
    {
        _brief = brief;
        _roomKey = Guid.NewGuid().ToString("N");
        _families.Clear(); _instances.Clear(); _recipes.Clear();
        _stamp = 0;
    }

    public Task<NativeSnapshot> EnsureRoomAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot());

    public Task<NativeSnapshot> ApplyAsync(BuildProposal proposal, NativeExpectation? expected, CancellationToken cancellationToken)
    {
        var brief = _brief ?? throw new InvalidOperationException("No design is bound.");
        expected?.Verify(Snapshot());
        foreach (var recipe in proposal.Recipes)
        {
            var size = RecipeRules.Validate(recipe, brief).Size;
            var revision = _families.TryGetValue(recipe.AssetId, out var old) ? old.Revision + 1 : 1;
            _recipes[recipe.AssetId] = recipe;
            _families[recipe.AssetId] = new FamilyReceipt(recipe.AssetId, $"FS_{recipe.AssetId}", "Default", "(in memory)",
                recipe.AssetId, recipe.AssetId, recipe.Solids.Count(), size, revision);
        }
        foreach (var placement in proposal.Placements)
            _instances[placement.Key] = (placement, _recipes[placement.AssetId]);
        foreach (var key in _instances.Keys.ToArray())
            _instances[key] = (_instances[key].Placement, _recipes[_instances[key].Placement.AssetId]);
        _stamp++;
        _lastCaptures = Array.Empty<CaptureReceipt>();
        return Task.FromResult(Snapshot());
    }

    public Task<NativeSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var brief = _brief ?? throw new InvalidOperationException("No design is bound.");
        var path = journal.PathFor("captures", $"room-{++_captures:D2}.svg");
        File.WriteAllText(path, AxonometricSvg.Render(_instances.Values.Select(i => (i.Recipe, i.Placement)).ToArray(), brief, brief.Title));
        _lastCaptures = NativeSnapshot.CaptureViews.Select(v => new CaptureReceipt(v, v, path, 800, 600, "isometric svg")).ToArray();
        return Task.FromResult(Snapshot());
    }

    public Task<ProjectChoice[]> ListProjectsAsync(CancellationToken cancellationToken) => Task.FromResult(Array.Empty<ProjectChoice>());
    public Task LoadFamiliesAsync(string projectKey, IReadOnlyList<string> assetIds, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ShowRoomAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CloseAsync() => Task.CompletedTask;
    public void Dispose() { }

    private NativeSnapshot Snapshot() => new(_roomKey, "In-memory room", _families.Values.OrderBy(f => f.AssetId).ToArray(),
        _instances.Values.Select(i =>
        {
            var bounds = PlacementRules.WorldBounds(i.Recipe, i.Placement);
            return new InstanceReceipt(i.Placement.Key, i.Placement.AssetId, i.Placement.Key, i.Placement.PositionM, i.Placement.RotationDegrees, bounds.Min, bounds.Max);
        }).ToArray(), _lastCaptures, _stamp);
}
