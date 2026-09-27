using System.Windows.Interop;
using Autodesk.Revit.UI;
using FamilyStudio.Core.Codex;
using FamilyStudio.Core.Config;
using FamilyStudio.Core.Pipeline;
using FamilyStudio.Revit.Native;
using FamilyStudio.Revit.UI;

namespace FamilyStudio.Revit;

/// <summary>
/// Owns the one View2Render window per Revit session, with its own session folder and Codex
/// connection (the same ChatGPT sign-in as Family Studio). The ribbon button captures the active view
/// every time it is pressed; the window's own Capture button does the same through an ExternalEvent.
/// </summary>
internal static class RenderLauncher
{
    private static RenderWindow? _window;
    private static RenderViewModel? _model;
    private static Func<UIApplication, ViewCapture>? _capture;
    private static bool _revitClosing;

    /// <summary>Runs in the ribbon command's API context, so the active view is captured right here.</summary>
    public static void Open(UIApplication application)
    {
        if (_window is not null && _model is not null && _capture is not null)
        {
            Capture(application, _model, _capture);
            _window.Reveal();
            return;
        }

        var pluginDirectory = System.IO.Path.GetDirectoryName(typeof(RenderLauncher).Assembly.Location)!;
        var environment = StudioEnvironment.Load(pluginDirectory);
        var journal = SessionJournal.CreateUnder(environment.OutputRoot);
        var version = typeof(RenderLauncher).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var codex = new CodexService(new CodexOptions(environment.CodexHome, environment.CodexPath, environment.CodexWorkingDirectory, version)) { Journal = journal };
        var dispatcher = new RevitDispatcher(application); // creates the ExternalEvent, so it must happen in this API context
        var captures = 0;
        Func<UIApplication, ViewCapture> capture = app => ViewCapturer.Capture(app, journal, ++captures);
        var model = new RenderViewModel(codex, new ViewRenderer(codex, journal), environment,
            token => dispatcher.RunAsync(capture, token));

        var window = new RenderWindow(model);
        new WindowInteropHelper(window) { Owner = application.MainWindowHandle };
        window.Closed += (_, _) =>
        {
            _window = null;
            _model = null;
            _capture = null;
            model.Dispose();
            codex.Dispose(); // stops the app-server processes now
            // The ExternalEvent and the Idling handler are released in Revit's API context: queued while
            // Revit runs, or right here in OnShutdown's context when Revit is closing.
            if (_revitClosing) dispatcher.Dispose();
            else _ = dispatcher.RunAsync(_ => { dispatcher.Dispose(); return true; }, CancellationToken.None);
        };
        _window = window;
        _model = model;
        _capture = capture;
        Capture(application, model, capture);
        window.Show();
        // Start inside a dispatcher operation, so every await in the view model resumes on the UI thread.
        window.Dispatcher.BeginInvoke(new Action(() => _ = model.StartAsync()));
    }

    private static void Capture(UIApplication application, RenderViewModel model, Func<UIApplication, ViewCapture> capture)
    {
        try { model.ShowCapture(capture(application)); }
        catch (Exception ex) { model.ShowCaptureProblem(ex.Message); }
    }

    /// <summary>Revit is closing: stop Codex immediately so no app-server process outlives Revit.</summary>
    public static void Shutdown()
    {
        _revitClosing = true;
        var window = _window;
        _window = null;
        window?.CloseForShutdown();
    }
}
