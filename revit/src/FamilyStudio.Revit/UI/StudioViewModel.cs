using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FamilyStudio.Core.Codex;
using FamilyStudio.Core.Config;
using FamilyStudio.Core.Model;
using FamilyStudio.Core.Pipeline;
using FamilyStudio.Core.Prompts;

namespace FamilyStudio.Revit.UI;

public enum Sheet { Brief, Reference, Build }

public enum Connection { Starting, CodexMissing, SignedOut, SigningIn, SignedIn, Failed }

public enum Tone { Neutral, Review, Success, Danger, Accent }

/// <summary>
/// Everything the Family Studio window shows and does. It turns user input into drafts, forwards
/// operations to the session, and mirrors session and account state back into bindable properties.
/// </summary>
public sealed class StudioViewModel : ObservableObject, IDisposable
{
    private readonly CodexService _codex;
    private readonly StudioSession _session;
    private readonly StudioEnvironment _environment;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private int _signInAttempt;
    private int _refreshQueued;
    private StudioBrief? _shownBrief;
    private string? _shownReferencePath;
    private string? _shownCaptureStamp;
    private string? _shownFamiliesStamp;
    private ReviewReport? _shownReview;
    private ActivityEntry? _shownActivity;
    private IReadOnlyList<CodexModel>? _shownModels;
    private bool _populating;
    private bool _disposed;

    public StudioViewModel(CodexService codex, StudioSession session, StudioEnvironment environment, string version)
    {
        _codex = codex;
        _session = session;
        _environment = environment;
        Version = version;

        Items = new ObservableCollection<ItemCard>();
        CollectionMaterials = new ObservableCollection<MaterialNote>();
        CollectionPresets = Presets.All.Where(p => p.Id != Presets.SingleId).ToArray();
        _selectedPreset = CollectionPresets.First(p => p.Id == "office");

        SignInCommand = new Command(() => _ = SignInAsync(deviceCode: false), () => Connection is Connection.SignedOut or Connection.Failed);
        SignInWithCodeCommand = new Command(() => _ = SignInAsync(deviceCode: true), () => Connection is Connection.SignedOut or Connection.Failed or Connection.SigningIn);
        CancelSignInCommand = new Command(() => _ = CancelSignInAsync(), () => Connection == Connection.SigningIn);
        SignOutCommand = new Command(() => _ = SignOutAsync(), () => IsSignedIn && !IsBusy);
        RetryCommand = new Command(() => _ = StartAsync(), () => Connection is Connection.CodexMissing or Connection.Failed);
        PrimaryCommand = new Command(RunPrimary, () => CanPrimary);
        CancelCommand = new Command(() => _session.Cancel(), () => IsBusy);
        ChoosePhotoCommand = new Command(ChoosePhoto, () => CanEditBrief);
        RemovePhotoCommand = new Command(() => SetPhoto(null), () => CanEditBrief && PhotoPath is not null);
        SelectSheetCommand = new Command<string>(s => CurrentSheet = Enum.Parse<Sheet>(s), s => CanOpen(Enum.Parse<Sheet>(s)));
        // The selected segment stays enabled (a disabled one would look greyed out); choosing it again does nothing.
        SingleModeCommand = new Command(() => { if (IsCollection) SwitchMode(collection: false); }, () => CanStartNew);
        CollectionModeCommand = new Command(() => { if (!IsCollection) SwitchMode(collection: true); }, () => CanStartNew);
        UsePresetCommand = new Command(() => StartNew(SelectedPreset.Id), () => CanStartNew);
        // New design is a clean sheet in either mode; the examples are one click away in the mode switch and presets.
        NewDesignCommand = new Command(() => StartNew(IsCollection ? "blank" : Presets.SingleId, IsCollection ? null : StudioDraft.EmptySingleItem()), () => CanStartNew);
        OpenAcceptedCommand = new Command(OpenAccepted, () => CanStartNew);
        AddFinishCommand = new Command(AddFinish, () => CanEditReview && Finishes.Count < StudioLimits.MaxMaterials);
        RemoveFinishCommand = new Command<FinishRow>(RemoveFinish, _ => CanEditReview && Finishes.Count > 1);
        EditDesignCommand = new Command(EditDesign, () => !IsBusy && _session.Brief is { IsSingleItem: true } && _session.IsAccepted);
        ReviewCommand = new Command(() => Run(s => _session.ReviewAsync(s)), () => !IsBusy && _session.HasFamilies);
        RefineCommand = new Command(Refine, () => !IsBusy && _session.HasFamilies && !IsCollection && !string.IsNullOrWhiteSpace(RevisionText));
        SaveFamilyCommand = new Command(SaveFamily, () => !IsBusy && _session.Snapshot?.Families.Length == 1);
        RefreshProjectsCommand = new Command(() => _ = RefreshProjectsAsync(), () => !IsBusy);
        ShowRoomCommand = new Command(() => _ = Guard(() => _session.ShowRoomAsync()), () => !IsBusy && _session.Snapshot is not null);
        SelectViewCommand = new Command<string>(v => PreviewView = v);
        OpenOutputsCommand = new Command(() => OpenFolder(_session.Journal.DirectoryPath));
        ToggleSettingsCommand = new Command(() => IsSettingsOpen = !IsSettingsOpen);
        DismissAlertCommand = new Command(() => Alert = null);

        _codex.StateChanged += OnBackgroundChange;
        _session.Changed += OnBackgroundChange;
        LoadDraft(PresetDraft(Presets.SingleId), Presets.Get(Presets.SingleId).Unit); // opens on the example roundel (photo, name, size, finish), ready to run
        Refresh();
    }

    public string Version { get; }

    // ---- connection ------------------------------------------------------------------------

    private Connection _connection = Connection.Starting;
    public Connection Connection { get => _connection; private set { if (Set(ref _connection, value)) RaiseEverything(); } }
    public bool IsSignedIn => Connection == Connection.SignedIn;
    public bool NeedsSignIn => Connection != Connection.SignedIn;

    private string _connectionMessage = "Connecting to Codex...";
    public string ConnectionMessage { get => _connectionMessage; private set => Set(ref _connectionMessage, value); }

    private string? _signInCode;
    public string? SignInCode { get => _signInCode; private set => Set(ref _signInCode, value); }

    private string? _signInUrl;
    public string? SignInUrl { get => _signInUrl; private set => Set(ref _signInUrl, value); }

    public string AccountLabel => _codex.Account?.Label ?? "Not signed in";
    public string PlanLabel => _codex.Account?.Plan is { Length: > 0 } plan ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(plan) + " plan" : "";
    public string CodexLabel => _codex.Executable is { } exe ? $"Codex {exe.Version} ({exe.Source})" : "Codex not found yet";
    public string CodexHomeLabel => _environment.UsesOwnCodexHome ? $"Family Studio's own sign-in folder: {_codex.CodexHome}" : _codex.CodexHome;
    public string EnvLabel => _environment.LoadedFiles.Count == 0 ? "No .env file found. Defaults are in use." : string.Join("\n", _environment.LoadedFiles);

    public async Task StartAsync()
    {
        Connection = Connection.Starting;
        ConnectionMessage = "Connecting to Codex...";
        try
        {
            await _codex.ConnectAsync(_lifetime.Token);
            LoadModels();
            Connection = _codex.Account is null ? Connection.SignedOut : Connection.SignedIn;
            ConnectionMessage = _codex.Account is null ? "Sign in with your ChatGPT account to start." : "";
        }
        catch (FileNotFoundException ex)
        {
            Connection = Connection.CodexMissing;
            ConnectionMessage = ex.Message;
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested)
        {
            Connection = Connection.Failed;
            ConnectionMessage = ex.Message;
        }
    }

    private async Task SignInAsync(bool deviceCode)
    {
        // "Use a code instead" can replace a browser sign-in that is still waiting. Only the newest
        // attempt may change the window; a replaced one ends quietly.
        var attempt = ++_signInAttempt;
        try
        {
            Connection = Connection.SigningIn;
            SignInCode = null;
            SignInUrl = null;
            ConnectionMessage = deviceCode ? "Requesting a sign-in code..." : "Opening your browser to sign in with ChatGPT...";
            var signIn = await _codex.BeginSignInAsync(deviceCode, _lifetime.Token);
            if (attempt != _signInAttempt) return;
            if (signIn.BrowserUrl is string url)
            {
                SignInUrl = url;
                OpenUrl(url);
                ConnectionMessage = "Finish signing in in your browser. This window updates when you are done.";
            }
            else
            {
                SignInCode = signIn.UserCode;
                SignInUrl = signIn.VerificationUrl;
                ConnectionMessage = "Open the link, sign in with ChatGPT and enter this code.";
            }
            var outcome = await signIn.Completion;
            if (attempt != _signInAttempt) return;
            SignInCode = null;
            if (outcome.Success)
            {
                LoadModels();
                Connection = Connection.SignedIn;
            }
            else
            {
                Connection = Connection.SignedOut;
                ConnectionMessage = outcome.Error ?? "Sign-in did not finish. Try again.";
            }
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested)
        {
            if (attempt != _signInAttempt) return;
            Connection = Connection.Failed;
            ConnectionMessage = ex.Message;
        }
    }

    private async Task CancelSignInAsync()
    {
        ++_signInAttempt;
        await _codex.CancelSignInAsync();
        SignInCode = null;
        Connection = Connection.SignedOut;
        ConnectionMessage = "Sign-in cancelled.";
    }

    private async Task SignOutAsync()
    {
        IsSettingsOpen = false;
        await Guard(() => _codex.SignOutAsync(_lifetime.Token));
        Connection = Connection.SignedOut;
        ConnectionMessage = "Signed out. Sign in with ChatGPT to continue.";
    }

    // ---- model settings --------------------------------------------------------------------

    public ObservableCollection<ModelOption> Models { get; } = new();

    private ModelOption? _selectedModel;
    public ModelOption? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!Set(ref _selectedModel, value) || value is null) return;
            Efforts = value.Efforts.ToArray();
            SelectedEffort = PreferredEffort(value);
            Raise(nameof(ModelLabel));
        }
    }

    private string[] _efforts = Array.Empty<string>();
    public string[] Efforts { get => _efforts; private set => Set(ref _efforts, value); }

    private string? _selectedEffort;
    public string? SelectedEffort { get => _selectedEffort; set { if (Set(ref _selectedEffort, value)) Raise(nameof(ModelLabel)); } }

    public Fidelity[] Fidelities => Fidelity.All;

    private Fidelity _selectedFidelity = Fidelity.Concept;
    public Fidelity SelectedFidelity { get => _selectedFidelity; set { if (Set(ref _selectedFidelity, value)) Raise(nameof(DetailLabel)); } }
    public string DetailCode { get => SelectedFidelity.Name; set => SelectedFidelity = Fidelity.All.FirstOrDefault(f => f.Name == value) ?? Fidelity.Concept; }

    public string ModelLabel => SelectedModel is null ? "No model" : $"{SelectedModel.Id} / {SelectedEffort}";
    public string DetailLabel => $"{SelectedFidelity.Name} {SelectedFidelity.Level}/10";

    private bool _isSettingsOpen;
    public bool IsSettingsOpen { get => _isSettingsOpen; set => Set(ref _isSettingsOpen, value); }

    private void LoadModels()
    {
        // Keep the user's choice when the catalog is read again.
        var (model, effort) = (SelectedModel?.Id, SelectedEffort);
        _shownModels = _codex.Models;
        Models.Clear();
        foreach (var m in _codex.Models)
            Models.Add(new ModelOption(m.Id, m.DisplayName, m.Description, m.Efforts, m.DefaultEffort));
        SelectedModel = Models.FirstOrDefault(m => m.Id == model)
            ?? Models.FirstOrDefault(m => m.Id == _environment.PreferredModel)
            ?? Models.FirstOrDefault(m => _codex.Models.First(c => c.Id == m.Id).IsDefault)
            ?? Models.FirstOrDefault();
        if (effort is not null && SelectedModel?.Id == model && Efforts.Contains(effort)) SelectedEffort = effort;
        RaiseEverything();
    }

    private string PreferredEffort(ModelOption model) =>
        _environment.PreferredEffort is string wanted && model.Efforts.Contains(wanted) ? wanted
        : CodexModel.StartingEffort(model.Efforts, model.DefaultEffort);

    private StageSettings Settings() => new(
        new ModelChoice(SelectedModel?.Id ?? throw new InvalidOperationException("Choose a model in Settings first."),
            SelectedEffort ?? SelectedModel.DefaultEffort),
        SelectedFidelity);

    // ---- sheets and mode -------------------------------------------------------------------

    private Sheet _currentSheet = Sheet.Brief;
    public Sheet CurrentSheet
    {
        get => _currentSheet;
        set
        {
            if (!Set(ref _currentSheet, value)) return;
            Raise(nameof(IsBriefSheet));
            Raise(nameof(IsReferenceSheet));
            Raise(nameof(IsBuildSheet));
            if (value == Sheet.Build) _ = RefreshProjectsAsync();
        }
    }


    public bool CanOpenReference => CanOpen(Sheet.Reference);
    public bool CanOpenBuild => CanOpen(Sheet.Build);
    public bool IsBriefSheet => CurrentSheet == Sheet.Brief;
    public bool IsReferenceSheet => CurrentSheet == Sheet.Reference;
    public bool IsBuildSheet => CurrentSheet == Sheet.Build;
    public bool ShowGenerating => IsBusy && _session.Reference is null && _session.State is StudioState.PreparingBrief or StudioState.GeneratingReference;
    public bool HasPreview => PreviewImage is not null;

    private bool CanOpen(Sheet sheet) => sheet switch
    {
        Sheet.Brief => true,
        Sheet.Reference => _session.Reference is not null,
        _ => _session.IsAccepted || _session.HasFamilies
    };

    private bool _isCollection;
    public bool IsCollection { get => _isCollection; private set { if (Set(ref _isCollection, value)) RaiseEverything(); } }
    public bool IsSingle => !IsCollection;

    public IReadOnlyList<Preset> CollectionPresets { get; }

    private Preset _selectedPreset;
    public Preset SelectedPreset { get => _selectedPreset; set => Set(ref _selectedPreset, value); }

    private bool CanStartNew => !IsBusy && IsSignedIn;

    private void SwitchMode(bool collection) => StartNew(collection ? SelectedPreset.Id : Presets.SingleId);

    /// <summary>The single item example's photo, copied next to the add-in by the build.</summary>
    private static readonly string ExamplePhoto = Path.Combine(
        Path.GetDirectoryName(typeof(StudioViewModel).Assembly.Location)!, "Examples", "trident-roundle.jpg");

    /// <summary>A preset's inputs. The single item example also gets its photo when the file is there.</summary>
    private static StudioDraft PresetDraft(string presetId)
    {
        var draft = Presets.Create(presetId);
        return presetId == Presets.SingleId && File.Exists(ExamplePhoto) ? draft with { ReferenceImagePath = ExamplePhoto } : draft;
    }

    private void StartNew(string presetId, StudioDraft? draft = null)
    {
        try
        {
            _session.StartNew(CurrentDraft(), presetId);
            LoadDraft(draft ?? PresetDraft(presetId), draft is null ? Presets.Get(presetId).Unit : LengthUnit.Millimetres);
            CurrentSheet = Sheet.Brief;
            Refresh();
        }
        catch (Exception ex) { Alert = ex.Message; }
    }

    // ---- brief inputs ----------------------------------------------------------------------

    private string _itemName = "";
    public string ItemName { get => _itemName; set { if (Set(ref _itemName, value)) DraftChanged(); } }

    private string _itemDescription = "";
    public string ItemDescription { get => _itemDescription; set { if (Set(ref _itemDescription, value)) DraftChanged(); } }

    private string _materialNotes = "";
    public string MaterialNotes { get => _materialNotes; set { if (Set(ref _materialNotes, value)) DraftChanged(); } }

    private string _widthText = "", _depthText = "", _heightText = "";
    public string WidthText { get => _widthText; set { if (Set(ref _widthText, value)) DraftChanged(); } }
    public string DepthText { get => _depthText; set { if (Set(ref _depthText, value)) DraftChanged(); } }
    public string HeightText { get => _heightText; set { if (Set(ref _heightText, value)) DraftChanged(); } }

    private string _unit = "mm";
    public string Unit
    {
        get => _unit;
        set
        {
            if (_unit == value) return;
            // Convert what was typed, so switching units never changes the size.
            Vec3? size = null;
            try { size = Dimensions.Parse(WidthText, DepthText, HeightText, UnitOf(_unit)); } catch (ArgumentException) { }
            _unit = value;
            if (size is not null)
            {
                _populating = true;
                WidthText = Dimensions.Format(size.X, UnitOf(value));
                DepthText = Dimensions.Format(size.Y, UnitOf(value));
                HeightText = Dimensions.Format(size.Z, UnitOf(value));
                _populating = false;
            }
            Raise();
            DraftChanged();
        }
    }

    private static LengthUnit UnitOf(string unit) => unit == "in" ? LengthUnit.Inches : LengthUnit.Millimetres;

    private string? _photoPath;
    public string? PhotoPath { get => _photoPath; private set { if (Set(ref _photoPath, value)) { Raise(nameof(HasPhoto)); DraftChanged(); } } }
    public bool HasPhoto => PhotoPath is not null;

    private ImageSource? _photoImage;
    public ImageSource? PhotoImage { get => _photoImage; private set => Set(ref _photoImage, value); }

    private string _photoCaption = "";
    public string PhotoCaption { get => _photoCaption; private set => Set(ref _photoCaption, value); }

    private string _roomStyle = "";
    public string RoomStyle { get => _roomStyle; set { if (Set(ref _roomStyle, value)) DraftChanged(); } }

    public ObservableCollection<ItemCard> Items { get; }
    public ObservableCollection<MaterialNote> CollectionMaterials { get; }

    public bool KnownSizeEntered
    {
        get
        {
            try { return Dimensions.Parse(WidthText, DepthText, HeightText, UnitOf(Unit)) is not null; }
            catch (ArgumentException) { return false; }
        }
    }

    public string? InputError
    {
        get
        {
            if (IsCollection) return null;
            if (Notes(MaterialNotes).Length > StudioLimits.MaxMaterialNotes) return "Use up to four finish notes, one per line.";
            try { Dimensions.Parse(WidthText, DepthText, HeightText, UnitOf(Unit)); return null; }
            catch (ArgumentException ex) { return ex.Message; }
        }
    }

    public bool CanEditBrief => !IsBusy && !_session.IsAccepted && IsSignedIn;

    /// <summary>The user chose, dropped or removed a photo.</summary>
    public void SetPhoto(string? path)
    {
        if (!CanEditBrief && path is not null) return;
        ShowPhoto(path);
    }

    /// <summary>Puts a photo on the plate. Restoring a design uses this too, which is why it has no edit guard.</summary>
    private void ShowPhoto(string? path)
    {
        if (path is null)
        {
            PhotoPath = null;
            PhotoImage = null;
            PhotoCaption = "";
            return;
        }
        try
        {
            var size = ReferenceImages.InspectUpload(path);
            PhotoImage = LoadImage(path);
            PhotoPath = path;
            PhotoCaption = $"{Path.GetFileName(path)}  ·  {size.Width} x {size.Height} px";
        }
        catch (Exception ex) { Alert = ex.Message; }
    }

    private void ChoosePhoto()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose a product photo", Filter = "PNG or JPEG image|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog() == true) SetPhoto(dialog.FileName);
    }

    private StudioDraft CurrentDraft()
    {
        if (IsCollection)
            return new StudioDraft(RoomStyle, Items.Select(i => i.Description).ToArray(), CollectionMaterials.Select(m => m.Text).ToArray(),
                Items.Select(i => i.Name).ToArray());
        Vec3? size = null;
        try { size = Dimensions.Parse(WidthText, DepthText, HeightText, UnitOf(Unit)); } catch (ArgumentException) { }
        return new StudioDraft("", new[] { ItemDescription }, Notes(MaterialNotes), new[] { ItemName }, PhotoPath, size);
    }

    private static string[] Notes(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

    private void LoadDraft(StudioDraft draft, LengthUnit unit = LengthUnit.Millimetres)
    {
        _populating = true;
        try
        {
            IsCollection = !draft.IsSingleItem;
            _itemName = draft.IsSingleItem ? draft.AssetNames[0] : "";
            _itemDescription = draft.IsSingleItem ? draft.Assets[0] : "";
            _materialNotes = draft.IsSingleItem ? string.Join("\n", draft.Materials) : "";
            _roomStyle = draft.IsSingleItem ? "" : draft.Style;
            _unit = Dimensions.Symbol(unit);
            _widthText = draft.KnownSizeM is null ? "" : Dimensions.Format(draft.KnownSizeM.X, unit);
            _depthText = draft.KnownSizeM is null ? "" : Dimensions.Format(draft.KnownSizeM.Y, unit);
            _heightText = draft.KnownSizeM is null ? "" : Dimensions.Format(draft.KnownSizeM.Z, unit);
            ShowPhoto(draft.ReferenceImagePath is string photo && File.Exists(photo) ? photo : null);

            foreach (var item in Items) item.Changed -= DraftChanged;
            foreach (var material in CollectionMaterials) material.Changed -= DraftChanged;
            Items.Clear();
            CollectionMaterials.Clear();
            if (!draft.IsSingleItem)
            {
                for (var i = 0; i < draft.Assets.Length; i++)
                {
                    var card = new ItemCard(i + 1, draft.AssetNames[i], draft.Assets[i]);
                    card.Changed += DraftChanged;
                    Items.Add(card);
                }
                for (var i = 0; i < StudioLimits.CollectionMaterials; i++)
                {
                    var note = new MaterialNote(i + 1, i < draft.Materials.Length ? draft.Materials[i] : "");
                    note.Changed += DraftChanged;
                    CollectionMaterials.Add(note);
                }
            }
        }
        finally
        {
            _populating = false;
            RaiseAll();
        }
    }

    private void DraftChanged()
    {
        if (_populating) return;
        _session.InvalidateDraft();
        Raise(nameof(InputError));
        Raise(nameof(KnownSizeEntered));
        RefreshActions();
    }

    // ---- reference and review --------------------------------------------------------------

    private ImageSource? _referenceImage;
    public ImageSource? ReferenceImage { get => _referenceImage; private set => Set(ref _referenceImage, value); }

    private string _referenceCaption = "";
    public string ReferenceCaption { get => _referenceCaption; private set => Set(ref _referenceCaption, value); }

    private string _reviewName = "", _reviewDescription = "", _reviewWidth = "", _reviewDepth = "", _reviewHeight = "";
    public string ReviewName { get => _reviewName; set => Set(ref _reviewName, value); }
    public string ReviewDescription { get => _reviewDescription; set => Set(ref _reviewDescription, value); }
    public string ReviewWidth { get => _reviewWidth; set => Set(ref _reviewWidth, value); }
    public string ReviewDepth { get => _reviewDepth; set => Set(ref _reviewDepth, value); }
    public string ReviewHeight { get => _reviewHeight; set => Set(ref _reviewHeight, value); }

    private bool _verified;
    public bool Verified { get => _verified; set => Set(ref _verified, value); }

    public ObservableCollection<FinishRow> Finishes { get; } = new();
    public ObservableCollection<ScheduleRow> Schedule { get; } = new();

    public bool CanEditReview => !IsBusy && _session.State == StudioState.Review && !_session.IsAccepted && _session.Brief is { IsSingleItem: true };

    private void ShowBrief(StudioBrief? brief)
    {
        if (ReferenceEquals(brief, _shownBrief)) return;
        _shownBrief = brief;
        Finishes.Clear();
        Schedule.Clear();
        if (brief is null) return;
        if (brief.IsSingleItem)
        {
            var asset = brief.Assets[0];
            ReviewName = asset.Name;
            ReviewDescription = asset.Description;
            ReviewWidth = Dimensions.Format(asset.SizeM.X, LengthUnit.Millimetres);
            ReviewDepth = Dimensions.Format(asset.SizeM.Y, LengthUnit.Millimetres);
            ReviewHeight = Dimensions.Format(asset.SizeM.Z, LengthUnit.Millimetres);
            Verified = asset.DimensionsConfirmed;
        }
        for (var i = 0; i < brief.Materials.Length; i++) Finishes.Add(new FinishRow(i, brief.Materials[i]));
        foreach (var asset in brief.Assets)
            Schedule.Add(new ScheduleRow($"FF-{asset.Id[1..].PadLeft(2, '0')}", asset.Name, $"x{asset.Quantity}", asset.SizeM.ToMillimetres(),
                asset.FloorStanding ? "Floor" : "On a surface"));
    }

    private void AddFinish() => Finishes.Add(new FinishRow(Finishes.Count, new MaterialBrief("", "New finish", "Where it is used", new[] { 160, 160, 160 })));

    private void RemoveFinish(FinishRow row)
    {
        Finishes.Remove(row);
        for (var i = 0; i < Finishes.Count; i++) Finishes[i].Renumber(i);
    }

    private void ApplyReviewEdits()
    {
        if (_session.Brief is not { IsSingleItem: true }) return;
        var size = Dimensions.Parse(ReviewWidth, ReviewDepth, ReviewHeight, LengthUnit.Millimetres)
            ?? throw new ArgumentException("Enter the width, depth and height of the family.");
        _session.UpdateSingleItem(ReviewName, ReviewDescription, size, Verified, Finishes.Select(f => f.ToBrief()).ToArray());
    }

    private void EditDesign()
    {
        try
        {
            _session.ReopenForEdits();
            CurrentSheet = Sheet.Reference;
        }
        catch (Exception ex) { Alert = ex.Message; }
    }

    private void OpenAccepted()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open an accepted design",
            Filter = "Accepted designs (accepted-*.json)|accepted-*.json",
            InitialDirectory = Directory.GetParent(_session.Journal.DirectoryPath)?.Parent?.FullName ?? _session.Journal.DirectoryPath
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var draft = _session.OpenAccepted(dialog.FileName);
            LoadDraft(draft);
            CurrentSheet = Sheet.Reference;
            Refresh();
        }
        catch (Exception ex) { Alert = ex.Message; }
    }

    // ---- build -----------------------------------------------------------------------------

    private string _previewView = "front-right";
    public string PreviewView { get => _previewView; set { if (Set(ref _previewView, value)) ShowCapture(force: true); } }

    private ImageSource? _previewImage;
    public ImageSource? PreviewImage { get => _previewImage; private set { if (Set(ref _previewImage, value)) Raise(nameof(HasPreview)); } }

    public string PreviewCaption => PreviewView switch
    {
        "plan" => $"FIG. 3  ·  PLAN  ·  {ScaleLabel}",
        "front-left" => "FIG. 3  ·  3D VIEW FROM FRONT LEFT",
        _ => "FIG. 3  ·  3D VIEW FROM FRONT RIGHT"
    };

    public ObservableCollection<FamilyRow> Families { get; } = new();
    public ObservableCollection<ProjectChoice> Projects { get; } = new();

    private ProjectChoice? _selectedProject;
    public ProjectChoice? SelectedProject { get => _selectedProject; set { if (Set(ref _selectedProject, value)) RefreshActions(); } }

    private string _revisionText = "";
    public string RevisionText { get => _revisionText; set => Set(ref _revisionText, value); }

    private bool _reviewAfterBuild;
    public bool ReviewAfterBuild { get => _reviewAfterBuild; set => Set(ref _reviewAfterBuild, value); }

    public ObservableCollection<FindingRow> Findings { get; } = new();
    public string ReviewSummary => _session.LastReview?.Summary ?? "";
    public bool HasReview => _session.LastReview is not null;

    private void ShowCapture(bool force = false)
    {
        var snapshot = _session.Snapshot;
        var stamp = snapshot is null ? null : $"{snapshot.DocumentKey}:{snapshot.ChangeStamp}:{snapshot.Captures.Length}:{PreviewView}";
        if (!force && stamp == _shownCaptureStamp) return;
        _shownCaptureStamp = stamp;
        var capture = snapshot?.Captures.FirstOrDefault(c => c.ViewKey == PreviewView);
        PreviewImage = capture is not null && File.Exists(capture.Path) ? LoadImage(capture.Path) : null;
        Raise(nameof(PreviewCaption));
    }

    // Lists are rebuilt only when their content changes, so progress updates never reset a checkbox mid-click.
    private void ShowFamilies()
    {
        var families = _session.Snapshot?.Families ?? Array.Empty<FamilyReceipt>();
        var stamp = string.Join("|", families.Select(f => $"{f.AssetId}:{f.Revision}:{f.FamilyName}"));
        if (stamp != _shownFamiliesStamp)
        {
            _shownFamiliesStamp = stamp;
            var deselected = Families.Where(f => !f.Selected).Select(f => f.AssetId).ToHashSet();
            Families.Clear();
            foreach (var family in families)
            {
                var row = new FamilyRow(family.AssetId, family.FamilyName, family.SizeM.ToMillimetres(),
                    $"{family.SolidParts} solids  ·  revision {family.Revision}") { Selected = !deselected.Contains(family.AssetId) };
                row.PropertyChanged += (_, _) => RefreshActions();
                Families.Add(row);
            }
        }

        var review = _session.LastReview;
        if (!ReferenceEquals(review, _shownReview))
        {
            _shownReview = review;
            Findings.Clear();
            foreach (var f in review?.Findings ?? Array.Empty<ReviewFinding>())
                Findings.Add(new FindingRow($"{f.Severity.ToUpperInvariant()}  ·  {f.Category.ToUpperInvariant()}  ·  {f.PlacementKey ?? f.AssetId}", f.Evidence, f.Correction, f.Severity == "major"));
        }
    }

    private void ShowActivity()
    {
        var entries = _session.Activity;
        var latest = entries.Count == 0 ? null : entries[entries.Count - 1];
        if (ReferenceEquals(latest, _shownActivity)) return;
        _shownActivity = latest;
        Activity.Clear();
        foreach (var entry in entries.Reverse().Take(40))
            Activity.Add(new ActivityRow(entry.Time.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), entry.Text,
                entry.Duration is TimeSpan d ? (d.TotalSeconds < 90 ? $"{d.TotalSeconds:0} s" : $"{d.TotalMinutes:0.0} min") : "", entry.IsError));
    }

    private async Task RefreshProjectsAsync()
    {
        if (_session.Snapshot is null && !_session.HasFamilies) return;
        try
        {
            var keep = SelectedProject?.Title;
            var projects = await _session.ListProjectsAsync(_lifetime.Token);
            Projects.Clear();
            foreach (var p in projects) Projects.Add(p);
            SelectedProject = Projects.FirstOrDefault(p => p.Title == keep) ?? (Projects.Count == 1 ? Projects[0] : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* the room may be busy; the user can refresh */ }
    }

    private void Refine() => Run(settings => _session.RefineAsync(RevisionText, settings), () => RevisionText = "");

    private void SaveFamily()
    {
        var family = _session.Snapshot?.Families.SingleOrDefault();
        if (family is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save the family", Filter = "Revit family (*.rfa)|*.rfa", FileName = Path.GetFileName(family.RfaPath) };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (!string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(family.RfaPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(family.RfaPath, dialog.FileName, overwrite: true);
            Alert = null;
        }
        catch (Exception ex) { Alert = ex.Message; }
    }

    // ---- status and the primary action -----------------------------------------------------

    public StudioState State => _session.State;
    public bool IsBusy => _session.IsBusy;
    public string Status => _session.Status;
    public string Detail => _session.Detail;
    public ObservableCollection<ActivityRow> Activity { get; } = new();

    private string? _alert;
    /// <summary>An inline redline note for input problems, separate from the session status.</summary>
    public string? Alert { get => _alert; set => Set(ref _alert, value); }

    public string TitleName => _session.Brief?.Title ?? (IsCollection ? "Untitled collection" : string.IsNullOrWhiteSpace(ItemName) ? "Untitled" : ItemName);
    public string ScaleLabel => IsCollection ? "1:50" : "1:20";

    public string StampLabel => State switch
    {
        StudioState.Ready => "Issued",
        StudioState.Built => "Built",
        StudioState.Review when _session.IsAccepted => "Checked",
        StudioState.Review => "For review",
        StudioState.Error or StudioState.TimedOut or StudioState.LimitReached or StudioState.DocumentUnavailable or StudioState.SignInRequired => "Not issued",
        _ when IsBusy => "In progress",
        _ => "Preliminary"
    };

    public Tone StampTone => State switch
    {
        StudioState.Ready or StudioState.Built => Tone.Success,
        StudioState.Review when _session.IsAccepted => Tone.Accent,
        StudioState.Review => Tone.Review,
        StudioState.Error or StudioState.TimedOut or StudioState.LimitReached or StudioState.DocumentUnavailable or StudioState.SignInRequired => Tone.Danger,
        _ => Tone.Neutral
    };

    public string PrimaryLabel
    {
        get
        {
            if (!IsSignedIn) return "Sign in with ChatGPT";
            if (IsBusy) return "Working";
            if (_session.HasFamilies) return "Load into project";
            if (_session.IsAccepted) return IsCollection ? "Build families" : "Build family";
            if (_session.State == StudioState.Review && _session.Reference is not null) return "Accept design";
            return IsCollection ? "Make reference sheet" : HasPhoto ? "Read the photo" : "Make reference";
        }
    }

    public bool CanPrimary
    {
        get
        {
            if (Connection is Connection.Starting or Connection.SigningIn or Connection.CodexMissing) return false;
            if (!IsSignedIn) return true;
            if (IsBusy || SelectedModel is null) return false;
            if (_session.HasFamilies) return SelectedProject is not null && Families.Any(f => f.Selected);
            if (_session.IsAccepted) return true;
            if (_session.State == StudioState.Review && _session.Reference is not null) return true;
            return InputError is null && CurrentDraft().IsComplete;
        }
    }

    public ICommand PrimaryCommand { get; }

    private void RunPrimary()
    {
        Alert = null;
        if (!IsSignedIn) { _ = SignInAsync(deviceCode: false); return; }
        if (_session.HasFamilies)
        {
            if (SelectedProject is { } project)
                Run(_ => _session.LoadAsync(project.Key, Families.Where(f => f.Selected).Select(f => f.AssetId).ToArray()));
            return;
        }
        if (_session.IsAccepted)
        {
            CurrentSheet = Sheet.Build;
            Run(settings => _session.BuildAsync(settings, ReviewAfterBuild), () => _ = RefreshProjectsAsync());
            return;
        }
        if (_session.State == StudioState.Review && _session.Reference is not null)
        {
            try
            {
                ApplyReviewEdits();
                _session.Accept(CurrentDraft());
                CurrentSheet = Sheet.Build;
            }
            catch (Exception ex) { Alert = ex.Message; }
            return;
        }
        if (InputError is string error) { Alert = error; return; }
        Run(settings => _session.GenerateAsync(CurrentDraft(), settings), () =>
        {
            if (_session.State == StudioState.Review) CurrentSheet = Sheet.Reference;
        });
    }

    private void Run(Func<StageSettings, Task> operation, Action? after = null)
    {
        StageSettings settings;
        try { settings = Settings(); }
        catch (Exception ex) { Alert = ex.Message; return; }
        _ = RunAsync();

        async Task RunAsync()
        {
            try { await operation(settings); }
            catch (Exception ex) { Alert = ex.Message; }
            after?.Invoke();
            Refresh();
        }
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Alert = ex.Message; }
    }

    // ---- commands ----------------------------------------------------------------------------

    public ICommand SignInCommand { get; }
    public ICommand SignInWithCodeCommand { get; }
    public ICommand CancelSignInCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ChoosePhotoCommand { get; }
    public ICommand RemovePhotoCommand { get; }
    public ICommand SelectSheetCommand { get; }
    public ICommand SingleModeCommand { get; }
    public ICommand CollectionModeCommand { get; }
    public ICommand UsePresetCommand { get; }
    public ICommand NewDesignCommand { get; }
    public ICommand OpenAcceptedCommand { get; }
    public ICommand AddFinishCommand { get; }
    public ICommand RemoveFinishCommand { get; }
    public ICommand EditDesignCommand { get; }
    public ICommand ReviewCommand { get; }
    public ICommand RefineCommand { get; }
    public ICommand SaveFamilyCommand { get; }
    public ICommand RefreshProjectsCommand { get; }
    public ICommand ShowRoomCommand { get; }
    public ICommand SelectViewCommand { get; }
    public ICommand OpenOutputsCommand { get; }
    public ICommand ToggleSettingsCommand { get; }
    public ICommand DismissAlertCommand { get; }

    // ---- refresh -----------------------------------------------------------------------------

    /// <summary>
    /// Session and account events can fire on any thread, and inside Revit's own events. The window
    /// always refreshes later, in its own dispatcher operation (with the UI thread's synchronization
    /// context), and a burst of events costs one refresh.
    /// </summary>
    private void OnBackgroundChange()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ui.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            Volatile.Write(ref _refreshQueued, 0);
            Refresh();
        }));
    }

    private void Refresh()
    {
        if (_disposed) return;
        if (Connection == Connection.SignedIn && _codex.Account is null) Connection = Connection.SignedOut;
        else if (Connection is Connection.SignedOut or Connection.SigningIn && _codex.Account is not null)
        {
            // Signed in some other way, such as a browser sign-in that finished after "Use a code instead".
            ++_signInAttempt;
            SignInCode = null;
            SignInUrl = null;
            ConnectionMessage = "";
            Connection = Connection.SignedIn;
        }
        if (!ReferenceEquals(_codex.Models, _shownModels)) LoadModels(); // the catalog is read again after every sign-in
        ShowBrief(_session.Brief);
        var reference = _session.Reference;
        if (reference?.Path != _shownReferencePath)
        {
            _shownReferencePath = reference?.Path;
            ReferenceImage = reference is not null && File.Exists(reference.Path) ? LoadImage(reference.Path) : null;
            ReferenceCaption = reference is null ? "" :
                $"FIG. 2  ·  {(reference.IsUpload ? "YOUR PHOTO" : "GENERATED REFERENCE")}  ·  {reference.Width} x {reference.Height} PX";
        }
        ShowCapture();
        ShowFamilies();
        ShowActivity();
        if (CurrentSheet == Sheet.Build && !CanOpen(Sheet.Build)) CurrentSheet = _session.Reference is null ? Sheet.Brief : Sheet.Reference;
        if (CurrentSheet == Sheet.Reference && !CanOpen(Sheet.Reference)) CurrentSheet = Sheet.Brief;
        RaiseEverything();
    }

    private void RefreshActions()
    {
        Raise(nameof(PrimaryLabel));
        Raise(nameof(CanPrimary));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Refreshes every binding and every command's enabled state. WPF only re-asks commands after
    /// input, so work that finishes on its own must ask for it, or buttons stay disabled.
    /// </summary>
    private void RaiseEverything()
    {
        RaiseAll();
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static ImageSource? LoadImage(string path) => Files.LoadImage(path, maxWidth: 1600);

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { /* the URL is shown in the window */ }
    }

    private static void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _codex.StateChanged -= OnBackgroundChange;
        _session.Changed -= OnBackgroundChange;
    }
}
