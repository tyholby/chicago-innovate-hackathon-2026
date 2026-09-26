using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using FamilyStudio.Core.Model;
using FamilyStudio.Core.Pipeline;
using FamilyStudio.Core.Validation;

namespace FamilyStudio.Revit.Native;

/// <summary>
/// The Revit side of Family Studio. Each accepted design gets its own preview room: a separate
/// project document with a floor, a back wall, a plan and two 3D views. Families are built into
/// the session folder, loaded into the room, placed, captured, and only loaded into a real project
/// when the user asks. Every call runs on Revit's API thread through the dispatcher.
/// </summary>
internal sealed class RevitStudioHost : IStudioHost
{
    private sealed class ProjectBinding(string key, Document document)
    {
        public string Key { get; } = key;
        public Document Document { get; } = document;
        public bool Available { get; set; } = true;
        public Dictionary<string, string> LoadedFamilies { get; } = new(StringComparer.Ordinal); // family name -> unique id
    }

    private readonly UIApplication _application;
    private readonly RevitDispatcher _dispatcher;
    private readonly SessionJournal _journal;
    private readonly string? _templateOverride;
    private readonly Dictionary<string, FamilyReceipt> _families = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string UniqueId, string AssetId)> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ElementId> _views = new(StringComparer.Ordinal);
    private readonly List<ProjectBinding> _projects = new();
    private StudioBrief? _brief;
    private Document? _room;
    private ElementId? _level;
    private string _roomKey = "";
    private string? _roomPath;
    private int _roomCount;
    private int _captureCount;
    private long _changeStamp;
    private bool _needsNewRoom;
    private bool _roomUnavailable;
    private bool _disposed;
    private CaptureReceipt[] _captures = Array.Empty<CaptureReceipt>();

    public event Action? DocumentUnavailable;
    public Task WhenIdle => _dispatcher.WhenIdle;

    public RevitStudioHost(UIApplication application, SessionJournal journal, string? templateOverride)
    {
        _application = application;
        _journal = journal;
        _templateOverride = templateOverride;
        _dispatcher = new RevitDispatcher(application);
        _application.Application.DocumentClosing += OnDocumentClosing;
        _application.Application.DocumentChanged += OnDocumentChanged;
    }

    public void BindDesign(StudioBrief brief)
    {
        brief.Validate();
        _brief = brief;
        _needsNewRoom = true; // every accepted design starts a fresh room; earlier rooms stay open and on disk
    }

    public Task<NativeSnapshot> EnsureRoomAsync(CancellationToken cancellationToken) =>
        _dispatcher.RunAsync(app => { EnsureRoom(app); return Snapshot(); }, cancellationToken);

    public Task<NativeSnapshot> ApplyAsync(BuildProposal proposal, NativeExpectation? expected, CancellationToken cancellationToken) =>
        _dispatcher.RunAsync(app => Apply(app, proposal, expected, cancellationToken), cancellationToken);

    public Task<NativeSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
        _dispatcher.RunAsync(_ => Capture(cancellationToken), cancellationToken);

    public Task<ProjectChoice[]> ListProjectsAsync(CancellationToken cancellationToken) => _dispatcher.RunAsync(app =>
    {
        foreach (Document document in app.Application.Documents)
        {
            if (!document.IsValidObject || document.IsFamilyDocument || document.IsLinked || Same(document, _room)) continue;
            if (!_projects.Any(p => p.Available && Same(p.Document, document)))
                _projects.Add(new ProjectBinding(Guid.NewGuid().ToString("N"), document));
        }
        return _projects.Where(p => p.Available && p.Document.IsValidObject && !Same(p.Document, _room))
            .Select(p => new ProjectChoice(p.Key, p.Document.Title)).ToArray();
    }, cancellationToken);

    public Task LoadFamiliesAsync(string projectKey, IReadOnlyList<string> assetIds, CancellationToken cancellationToken) =>
        _dispatcher.RunAsync(_ => { LoadFamilies(projectKey, assetIds, cancellationToken); return true; }, cancellationToken);

    public Task ShowRoomAsync(CancellationToken cancellationToken) => _dispatcher.RunAsync(app =>
    {
        var room = RequireRoom();
        var ui = app.OpenAndActivateDocument(room.PathName);
        if (_views.TryGetValue("front-right", out var view) && room.GetElement(view) is View v) ui.ActiveView = v;
        return true;
    }, cancellationToken);

    public Task CloseAsync() => _disposed ? Task.CompletedTask : _dispatcher.RunAsync(_ => { Dispose(); return true; }, CancellationToken.None);

    // ---- room ------------------------------------------------------------------------------

    private void EnsureRoom(UIApplication app)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var brief = _brief ?? throw new InvalidOperationException("Accept a design before building.");
        if (!_needsNewRoom)
        {
            RequireRoom();
            return;
        }

        _families.Clear(); _instances.Clear(); _views.Clear();
        _captures = Array.Empty<CaptureReceipt>();
        _changeStamp = 0;
        _roomUnavailable = false;
        _roomKey = Guid.NewGuid().ToString("N");
        var document = app.Application.NewProjectDocument(UnitSystem.Metric)
            ?? throw new InvalidOperationException("Revit could not create the preview room.");
        using (var transaction = new Transaction(document, "Family Studio: preview room"))
        {
            transaction.Start();
            var level = Level.Create(document, 0);
            level.Name = "Family Studio floor";
            _level = level.Id;
            BuildShell(document, level);

            var focus = brief.IsSingleItem ? brief.Assets[0].SizeM : null;
            var planType = ViewType(document, ViewFamily.FloorPlan);
            var plan = ViewPlan.Create(document, planType, level.Id);
            plan.Name = "Family Studio plan";
            plan.Scale = brief.IsSingleItem ? 20 : 50;
            plan.AreAnnotationCategoriesHidden = true;
            if (focus is not null)
            {
                plan.CropBox = FocusBox(focus);
                plan.CropBoxActive = true;
                plan.CropBoxVisible = false;
            }
            _views["plan"] = plan.Id;
            _views["front-left"] = Create3D(document, "Family Studio front-left", new Vec3(-7, -8, 5), focus);
            _views["front-right"] = Create3D(document, "Family Studio front-right", new Vec3(7, -8, 6), focus);
            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit did not commit the preview room.");
        }

        _roomPath = _journal.PathFor("preview-rooms", $"Family Studio preview {++_roomCount}.rvt");
        document.SaveAs(_roomPath, new SaveAsOptions { OverwriteExistingFile = true });
        var ui = app.OpenAndActivateDocument(_roomPath);
        _room = ui.Document;
        if (_room.GetElement(_views["front-right"]) is View front) ui.ActiveView = front;
        _needsNewRoom = false;
        _journal.Write("room_created", new { roomKey = _roomKey, path = _roomPath });
    }

    private static void BuildShell(Document document, Level level)
    {
        var w = StudioLimits.RoomHalfWidthM;
        var d = StudioLimits.RoomHalfDepthM;
        var outline = CurveLoop.Create(new List<Curve>
        {
            Line.CreateBound(Units.Point(-w, -d, 0), Units.Point(w, -d, 0)),
            Line.CreateBound(Units.Point(w, -d, 0), Units.Point(w, d, 0)),
            Line.CreateBound(Units.Point(w, d, 0), Units.Point(-w, d, 0)),
            Line.CreateBound(Units.Point(-w, d, 0), Units.Point(-w, -d, 0))
        });
        if (new FilteredElementCollector(document).OfClass(typeof(FloorType)).FirstElementId() is { } floorType && floorType != ElementId.InvalidElementId)
            Floor.Create(document, new List<CurveLoop> { outline }, floorType, level.Id);
        var wallType = new FilteredElementCollector(document).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault(t => t.Kind == WallKind.Basic);
        if (wallType is not null)
            Wall.Create(document, Line.CreateBound(Units.Point(-w, d + 0.1, 0), Units.Point(w, d + 0.1, 0)), wallType.Id, level.Id, Units.Feet(2.8), 0, false, false);
    }

    private ElementId Create3D(Document document, string name, Vec3 eye, Vec3? focus)
    {
        var view = View3D.CreateIsometric(document, ViewType(document, ViewFamily.ThreeDimensional));
        view.Name = name;
        var target = focus is null ? new Vec3(0, 0, 0.6) : new Vec3(0, 0, focus.Z * 0.45);
        var eyePoint = Units.Point(eye);
        var forward = (Units.Point(target) - eyePoint).Normalize();
        var right = forward.CrossProduct(XYZ.BasisZ).Normalize();
        var up = right.CrossProduct(forward).Normalize();
        view.SetOrientation(new ViewOrientation3D(eyePoint, up, forward));
        view.DisplayStyle = DisplayStyle.ShadingWithEdges;
        view.DetailLevel = ViewDetailLevel.Fine;
        view.AreAnnotationCategoriesHidden = true;
        view.SetSectionBox(focus is null
            ? new BoundingBoxXYZ { Min = Units.Point(-4.2, -3.2, -0.3), Max = Units.Point(4.2, 3.3, 3) }
            : FocusBox(focus));
        view.IsSectionBoxActive = true;
        return view.Id;
    }

    private static BoundingBoxXYZ FocusBox(Vec3 size) => new()
    {
        Min = Units.Point(-size.X / 2 - 0.25, -size.Y / 2 - 0.25, -0.05),
        Max = Units.Point(size.X / 2 + 0.25, size.Y / 2 + 0.25, size.Z + 0.25)
    };

    private static ElementId ViewType(Document document, ViewFamily family) =>
        new FilteredElementCollector(document).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .First(t => t.ViewFamily == family).Id;

    // ---- build -----------------------------------------------------------------------------

    private NativeSnapshot Apply(UIApplication app, BuildProposal proposal, NativeExpectation? expected, CancellationToken cancellationToken)
    {
        EnsureRoom(app);
        var room = RequireRoom();
        var brief = _brief!;
        expected?.Verify(Snapshot());
        foreach (var recipe in proposal.Recipes) RecipeRules.Validate(recipe, brief);

        var families = new Dictionary<string, FamilyReceipt>(_families, StringComparer.Ordinal);
        var instances = new Dictionary<string, (string UniqueId, string AssetId)>(_instances, StringComparer.Ordinal);
        using var group = new TransactionGroup(room, "Family Studio: build");
        group.Start();
        try
        {
            foreach (var recipe in proposal.Recipes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var asset = brief.Assets.Single(a => a.Id == recipe.AssetId);
                var name = UniqueFamilyName(brief, asset);
                var revision = families.TryGetValue(asset.Id, out var previous) ? previous.Revision + 1 : 1;
                var path = _journal.PathFor("families", asset.Id, $"revision-{revision:D2}", name + ".rfa");
                var built = FamilyBuilder.Create(app.Application, path, name, recipe, brief, _templateOverride);

                using var load = new Transaction(room, $"Family Studio: load {name}");
                load.Start();
                if (!room.LoadFamily(path, new OverwriteFamilyLoadOptions(), out var family) || family is null)
                    family = new FilteredElementCollector(room).OfClass(typeof(Family)).Cast<Family>().SingleOrDefault(f => f.Name == name)
                        ?? throw new InvalidOperationException($"Revit did not load {name}.");
                var symbols = family.GetFamilySymbolIds().Select(id => (FamilySymbol)room.GetElement(id)).ToArray();
                var symbol = symbols.FirstOrDefault(s => s.Name == built.TypeName) ?? symbols.First();
                if (!symbol.IsActive) symbol.Activate();
                room.Regenerate();
                Commit(load);
                families[asset.Id] = new FamilyReceipt(asset.Id, name, built.TypeName, path, family.UniqueId, symbol.UniqueId,
                    recipe.Parts.Length, built.SizeM, revision);
            }

            using (var place = new Transaction(room, "Family Studio: arrange"))
            {
                place.Start();
                var level = (Level)room.GetElement(_level!);
                foreach (var placement in proposal.Placements)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var receipt = families[placement.AssetId];
                    var target = Units.Point(placement.PositionM);
                    FamilyInstance instance;
                    if (instances.TryGetValue(placement.Key, out var existing))
                    {
                        instance = room.GetElement(existing.UniqueId) as FamilyInstance
                            ?? throw new StudioDocumentException($"Instance {placement.Key} was deleted from the preview room.");
                        ElementTransformUtils.MoveElement(room, instance.Id, target - ((LocationPoint)instance.Location).Point);
                    }
                    else
                    {
                        instance = room.Create.NewFamilyInstance(target, (FamilySymbol)room.GetElement(receipt.SymbolUniqueId), level, StructuralType.NonStructural);
                        instances[placement.Key] = (instance.UniqueId, placement.AssetId);
                    }
                    room.Regenerate();
                    var turn = Normalize(placement.RotationDegrees * Math.PI / 180 - ((LocationPoint)instance.Location).Rotation);
                    if (Math.Abs(turn) > 1e-9)
                        ElementTransformUtils.RotateElement(room, instance.Id, Line.CreateBound(target, target + XYZ.BasisZ), turn);
                }
                room.Regenerate();
                Commit(place);
            }
            if (group.Assimilate() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit did not commit the build.");
        }
        catch
        {
            if (group.HasStarted()) group.RollBack();
            _journal.Write("build_rolled_back", new { roomKey = _roomKey });
            throw;
        }

        Replace(_families, families);
        Replace(_instances, instances);
        _captures = Array.Empty<CaptureReceipt>();
        room.Save();
        var snapshot = Snapshot();
        _journal.Write("build_committed", new { roomKey = _roomKey, families = snapshot.Families.Length, instances = snapshot.Instances.Length });
        return snapshot;
    }

    private string UniqueFamilyName(StudioBrief brief, AssetBrief asset)
    {
        var name = FamilyBuilder.SafeName(asset.Name);
        return brief.Assets.Count(a => FamilyBuilder.SafeName(a.Name) == name) > 1 ? $"{name} {asset.Id.ToUpperInvariant()}" : name;
    }

    // ---- capture ---------------------------------------------------------------------------

    private NativeSnapshot Capture(CancellationToken cancellationToken)
    {
        var room = RequireRoom();
        var folder = _journal.PathFor("captures", $"{++_captureCount:D2}", "x");
        folder = System.IO.Path.GetDirectoryName(folder)!;
        var captures = new List<CaptureReceipt>();
        foreach (var (key, id) in _views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var view = room.GetElement(id) as View ?? throw new StudioDocumentException("A preview view was deleted.");
            var viewFolder = System.IO.Path.Combine(folder, key);
            Directory.CreateDirectory(viewFolder);
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = System.IO.Path.Combine(viewFolder, key),
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                PixelSize = 1600,
                FitDirection = FitDirectionType.Horizontal,
                ZoomType = ZoomFitType.FitToPage
            };
            options.SetViewsAndSheets(new List<ElementId> { id });
            room.ExportImage(options);
            var file = Directory.GetFiles(viewFolder, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                ?? throw new InvalidOperationException($"Revit did not export the {key} view.");
            var size = ReferenceImages.Measure(File.ReadAllBytes(file)) ?? new ImageSize(0, 0);
            var camera = view is View3D threeD
                ? $"3D from {Units.Vec(threeD.GetOrientation().EyePosition)} toward the room centre"
                : $"plan at 1:{view.Scale}";
            captures.Add(new CaptureReceipt(key, view.UniqueId, file, size.Width, size.Height, camera));
        }
        _captures = captures.ToArray();
        _journal.Write("captured", new { roomKey = _roomKey, folder });
        return Snapshot();
    }

    // ---- deliver ---------------------------------------------------------------------------

    private void LoadFamilies(string projectKey, IReadOnlyList<string> assetIds, CancellationToken cancellationToken)
    {
        var binding = _projects.SingleOrDefault(p => p.Key == projectKey && p.Available)
            ?? throw new StudioDocumentException("Choose an open project to load into.");
        var target = binding.Document;
        if (!target.IsValidObject || target.IsReadOnly || target.IsFamilyDocument)
            throw new StudioDocumentException("That project can no longer accept families. Choose another one.");
        if (assetIds.Count == 0) throw new ArgumentException("Choose at least one family to load.");

        using var group = new TransactionGroup(target, "Family Studio: load families");
        group.Start();
        foreach (var assetId in assetIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = _families.TryGetValue(assetId, out var r) ? r : throw new ArgumentException("That family has not been built yet.");
            var path = receipt.RfaPath;
            var existing = new FilteredElementCollector(target).OfClass(typeof(Family)).Cast<Family>().SingleOrDefault(f => f.Name == receipt.FamilyName);
            if (existing is not null && !(binding.LoadedFamilies.TryGetValue(receipt.FamilyName, out var owned) && owned == existing.UniqueId))
            {
                // Never overwrite someone else's family: load ours under the next free name instead.
                var name = receipt.FamilyName;
                for (var i = 2; new FilteredElementCollector(target).OfClass(typeof(Family)).Cast<Family>().Any(f => f.Name == name); i++)
                    name = $"{receipt.FamilyName} {i}";
                path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(receipt.RfaPath)!, name + ".rfa");
                File.Copy(receipt.RfaPath, path, overwrite: true);
            }
            using var load = new Transaction(target, "Family Studio: load family");
            load.Start();
            if (!target.LoadFamily(path, new OverwriteFamilyLoadOptions(), out var family) || family is null)
                family = new FilteredElementCollector(target).OfClass(typeof(Family)).Cast<Family>()
                    .SingleOrDefault(f => f.Name == System.IO.Path.GetFileNameWithoutExtension(path))
                    ?? throw new InvalidOperationException("Revit did not load the family.");
            Commit(load);
            binding.LoadedFamilies[family.Name] = family.UniqueId;
        }
        if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Revit did not commit the load.");
        _journal.Write("families_loaded", new { target = target.Title, assetIds });
    }

    // ---- state -----------------------------------------------------------------------------

    private NativeSnapshot Snapshot()
    {
        var room = RequireRoom();
        var instances = _instances.Select(pair =>
        {
            var instance = room.GetElement(pair.Value.UniqueId) as FamilyInstance
                ?? throw new StudioDocumentException($"Instance {pair.Key} was deleted from the preview room.");
            var location = (LocationPoint)instance.Location;
            var bounds = Units.Measure(new[] { instance });
            return new InstanceReceipt(pair.Key, pair.Value.AssetId, instance.UniqueId, Units.Vec(location.Point),
                location.Rotation * 180 / Math.PI, bounds.Min, bounds.Max);
        }).ToArray();
        return new NativeSnapshot(_roomKey, room.Title, _families.Values.OrderBy(f => f.AssetId, StringComparer.Ordinal).ToArray(),
            instances, _captures, _changeStamp);
    }

    private Document RequireRoom()
    {
        if (_roomUnavailable || _room is null || !_room.IsValidObject)
            throw new StudioDocumentException("The preview room is closed. Build again to open a new one.");
        return _room;
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs args)
    {
        if (!Same(args.GetDocument(), _room)) return;
        _changeStamp++;
        _captures = Array.Empty<CaptureReceipt>();
    }

    private void OnDocumentClosing(object? sender, DocumentClosingEventArgs args)
    {
        foreach (var project in _projects.Where(p => Same(p.Document, args.Document))) project.Available = false;
        if (!Same(args.Document, _room)) return;
        _roomUnavailable = true;
        _needsNewRoom = true;
        _journal.Write("room_closed", new { roomKey = _roomKey });
        DocumentUnavailable?.Invoke();
    }

    // Revit hands out different wrappers for the same document; compare only live handles, inside API callbacks.
    private static bool Same(Document? a, Document? b) => a is { IsValidObject: true } && b is { IsValidObject: true } && a.Equals(b);

    private static void Commit(Transaction transaction)
    {
        if (transaction.Commit() != TransactionStatus.Committed)
            throw new InvalidOperationException($"Revit did not commit \"{transaction.GetName()}\".");
    }

    private static double Normalize(double radians) => Math.Atan2(Math.Sin(radians), Math.Cos(radians));

    private static void Replace<T>(Dictionary<string, T> target, Dictionary<string, T> source)
    {
        target.Clear();
        foreach (var pair in source) target[pair.Key] = pair.Value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _application.Application.DocumentClosing -= OnDocumentClosing;
        _application.Application.DocumentChanged -= OnDocumentChanged;
        _dispatcher.Dispose();
    }
}
