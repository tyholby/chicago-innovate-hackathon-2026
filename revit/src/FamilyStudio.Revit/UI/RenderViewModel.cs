using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FamilyStudio.Core.Codex;
using FamilyStudio.Core.Config;
using FamilyStudio.Core.Model;
using FamilyStudio.Core.Pipeline;
using FamilyStudio.Core.Prompts;

namespace FamilyStudio.Revit.UI;

/// <summary>A reference image attached to the next render.</summary>
public sealed record RenderReference(string Path, string Caption, ImageSource? Thumbnail);

/// <summary>
/// Everything the View2Render window shows and does: the captured view, the optional prompt and
/// reference images, the render and its progress, and saving the result to the computer.
/// </summary>
public sealed class RenderViewModel : ObservableObject, IDisposable
{
    private readonly CodexService _codex;
    private readonly ViewRenderer _renderer;
    private readonly StudioEnvironment _environment;
    private readonly Func<CancellationToken, Task<ViewCapture>> _captureView;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RenderProgress _progress = new();
    private CancellationTokenSource? _rendering;
    private ViewCapture? _capture;
    private ViewCapture? _renderedFrom;
    private ViewRender? _render;
    private string _connectionMessage = "Connecting to ChatGPT through Codex...";
    private string _status = "Capture a view to begin.";
    private int _refreshQueued;
    private bool _disposed;

    /// <param name="captureView">Captures the active Revit view, in Revit's API context.</param>
    public RenderViewModel(CodexService codex, ViewRenderer renderer, StudioEnvironment environment, Func<CancellationToken, Task<ViewCapture>> captureView)
    {
        _codex = codex;
        _renderer = renderer;
        _environment = environment;
        _captureView = captureView;
        References = new ObservableCollection<RenderReference>();
        References.CollectionChanged += (_, _) =>
        {
            Raise(nameof(HasReferences));
            Raise(nameof(ReferenceCount));
            Raise(nameof(PromptPreview));
            CommandManager.InvalidateRequerySuggested();
        };

        PrimaryCommand = new Command(RunPrimary, () => CanPrimary);
        CancelCommand = new Command(() => _rendering?.Cancel(), () => IsBusy);
        CaptureCommand = new Command(() => _ = CaptureAsync(), () => !IsBusy && !IsCapturing);
        AddReferencesCommand = new Command(ChooseReferences, () => CanAddReferences);
        RemoveReferenceCommand = new Command<RenderReference>(r => References.Remove(r), _ => !IsBusy);
        ShowViewCommand = new Command(() => ShowingRender = false);
        ShowRenderCommand = new Command(() => ShowingRender = true);
        DownloadCommand = new Command(Download, () => CanDownload);
        RevealSavedCommand = new Command(() => { if (SavedPath is string path) Files.Reveal(path); }, () => SavedPath is not null);
        OpenFolderCommand = new Command(() => Files.Open(_renderer.Journal.DirectoryPath));
        DismissAlertCommand = new Command(() => Alert = null);

        _codex.StateChanged += OnBackgroundChange;
        _codex.Progress += OnProgress;
    }

    // ---- connection ------------------------------------------------------------------------

    private Connection _connection = Connection.Starting;
    public Connection Connection => _connection;
    public bool IsSignedIn => _connection == Connection.SignedIn;
    public string AccountLabel => _codex.Account?.Label ?? "Not signed in";

    private void SetConnection(Connection connection, string message)
    {
        _connection = connection;
        _connectionMessage = message;
        RaiseEverything();
    }

    public async Task StartAsync()
    {
        SetConnection(Connection.Starting, "Connecting to ChatGPT through Codex...");
        try
        {
            await _codex.ConnectAsync(_lifetime.Token);
            if (_codex.Account is null) SetConnection(Connection.SignedOut, "Sign in with your ChatGPT account to render.");
            else SetConnection(Connection.SignedIn, "");
        }
        catch (FileNotFoundException ex) { SetConnection(Connection.CodexMissing, ex.Message); }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested) { SetConnection(Connection.Failed, ex.Message); }
    }

    private async Task SignInAsync()
    {
        try
        {
            SetConnection(Connection.SigningIn, "Opening your browser to sign in with ChatGPT...");
            var signIn = await _codex.BeginSignInAsync(deviceCode: false, _lifetime.Token);
            if (signIn.BrowserUrl is string url) Files.Open(url);
            SetConnection(Connection.SigningIn, "Finish signing in in your browser. This window updates when you are done.");
            var outcome = await signIn.Completion;
            if (outcome.Success) SetConnection(Connection.SignedIn, "");
            else SetConnection(Connection.SignedOut, outcome.Error ?? "Sign-in did not finish. Try again.");
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested) { SetConnection(Connection.Failed, ex.Message); }
    }

    // ---- the view --------------------------------------------------------------------------

    private ImageSource? _captureImage;
    public ImageSource? CaptureImage { get => _captureImage; private set => Set(ref _captureImage, value); }
    public bool HasCapture => _capture is not null;
    public string ViewTitle => _capture?.ViewName ?? "No view yet";
    public string ViewDetail => _capture is { } c ? $"{c.ViewKind} in {c.DocumentName}" : "";

    private string? _captureProblem = "Open a 3D view, plan, section or elevation in Revit, then capture it.";
    public string? CaptureProblem { get => _captureProblem; private set => Set(ref _captureProblem, value); }

    private bool _isCapturing;
    public bool IsCapturing { get => _isCapturing; private set { if (Set(ref _isCapturing, value)) RaiseEverything(); } }

    /// <summary>Shows a new capture of the active view. Runs on the UI thread.</summary>
    public void ShowCapture(ViewCapture capture)
    {
        _capture = capture;
        CaptureImage = Files.LoadImage(capture.Path, maxWidth: 2048);
        CaptureProblem = null;
        _showingRender = false;
        _status = $"Captured {capture.ViewName}. Add a prompt or reference images if you like, then render.";
        RaiseEverything();
    }

    /// <summary>The active view could not be captured. A capture shown before stays.</summary>
    public void ShowCaptureProblem(string message)
    {
        if (_capture is null) CaptureProblem = message;
        else Alert = message;
        RaiseEverything();
    }

    private async Task CaptureAsync()
    {
        IsCapturing = true;
        try { ShowCapture(await _captureView(_lifetime.Token)); }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested) { ShowCaptureProblem(ex.Message); }
        finally { if (!_disposed) IsCapturing = false; }
    }

    // ---- prompt and references ---------------------------------------------------------------

    private string _userPrompt = "";
    public string UserPrompt { get => _userPrompt; set { if (Set(ref _userPrompt, value)) Raise(nameof(PromptPreview)); } }

    /// <summary>Exactly what the next render sends: the default brief, the references note and the user's words.</summary>
    public string PromptPreview => RenderPrompts.Compose(UserPrompt, References.Count);

    public ObservableCollection<RenderReference> References { get; }
    public bool HasReferences => References.Count > 0;
    public string ReferenceCount => $"{References.Count} of {ViewRenderer.MaxReferenceImages}";
    public bool CanAddReferences => !IsBusy && References.Count < ViewRenderer.MaxReferenceImages;
    public bool CanEdit => !IsBusy;

    /// <summary>Adds reference images, from the file dialog or dropped on the window. Problem files are reported and skipped.</summary>
    public void AddReferences(IEnumerable<string> paths)
    {
        if (IsBusy) return;
        var problems = new List<string>();
        foreach (var path in paths)
        {
            if (References.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            if (References.Count >= ViewRenderer.MaxReferenceImages)
            {
                problems.Add($"Use up to {ViewRenderer.MaxReferenceImages} reference images.");
                break;
            }
            try
            {
                var size = ReferenceImages.InspectUpload(path);
                var name = Path.GetFileName(path);
                References.Add(new RenderReference(path, $"{name}  ·  {size.Width} x {size.Height} px", Files.LoadImage(path, maxWidth: 240)));
            }
            catch (ArgumentException ex) { problems.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        Alert = problems.Count == 0 ? null : string.Join(" ", problems);
    }

    private void ChooseReferences()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose reference images",
            Filter = "PNG or JPEG images|*.png;*.jpg;*.jpeg",
            Multiselect = true
        };
        if (dialog.ShowDialog() == true) AddReferences(dialog.FileNames);
    }

    // ---- render --------------------------------------------------------------------------------

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) RaiseEverything(); } }
    public bool IsWorking => IsBusy || IsCapturing || _connection is Connection.Starting or Connection.SigningIn;

    private double _progressPercent;
    public double ProgressPercent { get => _progressPercent; private set { if (Set(ref _progressPercent, value)) Raise(nameof(ProgressLabel)); } }
    public string ProgressLabel => $"{Math.Floor(ProgressPercent):0} %";

    private string _progressText = "";
    public string ProgressText { get => _progressText; private set { if (Set(ref _progressText, value)) Raise(nameof(StatusLine)); } }

    private ImageSource? _renderImage;
    public ImageSource? RenderImage { get => _renderImage; private set => Set(ref _renderImage, value); }
    public bool HasRender => _render is not null;

    private bool _showingRender;
    public bool ShowingRender { get => _showingRender; set { if (Set(ref _showingRender, value)) RaiseAll(); } }
    public bool ShowingView => !ShowingRender;
    public bool ShowRenderImage => !IsBusy && HasRender;
    public bool ShowRenderEmpty => !IsBusy && !HasRender;

    /// <summary>The plate hides its dashed empty frame whenever it shows an image.</summary>
    public bool HasFigure => ShowingRender ? IsBusy || HasRender : HasCapture;

    public string FigureCaption => ShowingRender
        ? _render is { } r ? $"FIG. 2  ·  Render of {_renderedFrom?.ViewName}  ·  {r.Width} x {r.Height} px  ·  {r.Elapsed.TotalSeconds:0} s" : ""
        : _capture is { } c ? $"FIG. 1  ·  {c.ViewKind}: {c.ViewName}  ·  {c.Width} x {c.Height} px" : "";

    public bool CanDownload => ShowingRender && HasRender && !IsBusy;

    public bool CanPrimary => _connection switch
    {
        Connection.Starting or Connection.SigningIn => false,
        Connection.SignedIn => !IsBusy && !IsCapturing && HasCapture,
        _ => true
    };

    public string PrimaryLabel => _connection switch
    {
        Connection.Starting => "Connecting...",
        Connection.SigningIn => "Signing in...",
        Connection.SignedOut => "Sign in with ChatGPT",
        Connection.CodexMissing or Connection.Failed => "Try again",
        _ => IsBusy ? "Rendering..." : HasRender ? "Render again" : "Render view"
    };

    private void RunPrimary()
    {
        switch (_connection)
        {
            case Connection.SignedOut: _ = SignInAsync(); break;
            case Connection.CodexMissing or Connection.Failed: _ = StartAsync(); break;
            case Connection.SignedIn: _ = RenderAsync(); break;
        }
    }

    private async Task RenderAsync()
    {
        if (_capture is not { } capture || IsBusy) return;
        var prompt = UserPrompt;
        var references = References.Select(r => r.Path).ToArray();
        Alert = null;
        SavedPath = null;
        _progress.Reset();
        ProgressPercent = 0;
        ProgressText = "Sending the view to ChatGPT";
        _showingRender = true;
        using var rendering = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _rendering = rendering;
        IsBusy = true;
        try
        {
            var model = ViewRenderer.ChooseModel(_codex.Models, _environment.PreferredModel);
            var render = await _renderer.RenderAsync(capture, prompt, references, model, rendering.Token);
            _progress.Complete();
            ProgressPercent = 100;
            _render = render;
            _renderedFrom = capture;
            RenderImage = Files.LoadImage(render.Path, maxWidth: 2048);
            _status = $"Rendered in {render.Elapsed.TotalSeconds:0} seconds. Download it, or change the prompt and render again.";
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            _status = "Render cancelled.";
        }
        catch (StudioSignInRequiredException ex) when (!_lifetime.IsCancellationRequested)
        {
            SetConnection(Connection.SignedOut, ex.Message);
        }
        catch (StudioLimitException ex) when (!_lifetime.IsCancellationRequested)
        {
            _status = "The render did not run.";
            Alert = ex.ResetsAt is DateTimeOffset reset
                ? $"Your ChatGPT usage limit was reached. It resets {reset.ToLocalTime():g}."
                : $"Your ChatGPT usage limit was reached. {ex.Message}";
        }
        catch (Exception ex) when (!_lifetime.IsCancellationRequested)
        {
            _status = "The render did not finish.";
            Alert = ex.Message;
        }
        finally
        {
            _rendering = null;
            if (!_disposed) IsBusy = false;
        }
    }

    /// <summary>Codex reports progress on its own threads, about once a second.</summary>
    private void OnProgress(StageProgress progress)
    {
        if (_disposed) return;
        _ui.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            if (_disposed || !IsBusy) return;
            _progress.Update(progress);
            ProgressPercent = _progress.Percent;
            ProgressText = _progress.Text;
        }));
    }

    // ---- download ------------------------------------------------------------------------------

    private string? _savedPath;
    public string? SavedPath { get => _savedPath; private set { if (Set(ref _savedPath, value)) CommandManager.InvalidateRequerySuggested(); } }

    /// <summary>Codex already saved the render on this computer, so downloading is a copy to wherever the user picks.</summary>
    private void Download()
    {
        if (_render is not { } render || !File.Exists(render.Path))
        {
            Alert = "The render file is missing from the session folder. Render again.";
            return;
        }
        var extension = Path.GetExtension(render.Path).ToLowerInvariant();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Download the render",
            InitialDirectory = Files.DownloadsFolder(),
            FileName = Files.SafeName($"{_renderedFrom?.DocumentName} {_renderedFrom?.ViewName} render {DateTime.Now:yyyy-MM-dd HHmm}") + extension,
            DefaultExt = extension,
            Filter = extension == ".png" ? "PNG image (*.png)|*.png" : "JPEG image (*.jpg)|*.jpg",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.Copy(render.Path, dialog.FileName, overwrite: true);
            SavedPath = dialog.FileName;
            _status = $"Saved to {dialog.FileName}";
            Alert = null;
        }
        catch (Exception ex) { Alert = $"The render could not be saved there: {ex.Message}"; }
        RaiseEverything();
    }

    // ---- title block -------------------------------------------------------------------------

    public string StatusLine => _connection == Connection.SignedIn
        ? IsBusy ? ProgressText : IsCapturing ? "Capturing the active view..." : _status
        : _connectionMessage;

    public string StampLabel => IsBusy ? "Rendering" : ShowingRender && HasRender ? "Rendered" : HasCapture ? "Captured" : "No view";
    public Tone StampTone => IsBusy ? Tone.Accent : ShowingRender && HasRender ? Tone.Success : HasCapture ? Tone.Neutral : Tone.Review;

    private string? _alert;
    public string? Alert { get => _alert; set => Set(ref _alert, value); }

    public ICommand PrimaryCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand AddReferencesCommand { get; }
    public ICommand RemoveReferenceCommand { get; }
    public ICommand ShowViewCommand { get; }
    public ICommand ShowRenderCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand RevealSavedCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand DismissAlertCommand { get; }

    /// <summary>Account events fire on any thread; the window refreshes later in its own dispatcher operation.</summary>
    private void OnBackgroundChange()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ui.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            Volatile.Write(ref _refreshQueued, 0);
            if (_disposed) return;
            if (_connection == Connection.SignedIn && _codex.Account is null)
                SetConnection(Connection.SignedOut, "Your ChatGPT sign-in ended. Sign in again to render.");
            else if (_connection == Connection.SignedOut && _codex.Account is not null)
                SetConnection(Connection.SignedIn, "");
            else RaiseEverything();
        }));
    }

    /// <summary>Refreshes every binding and every command's enabled state (WPF only re-asks commands after input).</summary>
    private void RaiseEverything()
    {
        RaiseAll();
        CommandManager.InvalidateRequerySuggested();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _codex.StateChanged -= OnBackgroundChange;
        _codex.Progress -= OnProgress;
    }
}
