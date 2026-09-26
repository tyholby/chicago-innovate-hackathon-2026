using System.Windows.Interop;
using Autodesk.Revit.UI;
using FamilyStudio.Core.Codex;
using FamilyStudio.Core.Config;
using FamilyStudio.Core.Pipeline;
using FamilyStudio.Revit.Native;
using FamilyStudio.Revit.UI;

namespace FamilyStudio.Revit;

/// <summary>Owns the one Family Studio window per Revit session, and everything that lives with it.</summary>
internal static class StudioLauncher
{
    private static StudioWindow? _window;
    private static bool _revitClosing;

    public static void Open(UIApplication application)
    {
        if (_window is not null)
        {
            _window.Reveal();
            return;
        }

        var pluginDirectory = System.IO.Path.GetDirectoryName(typeof(StudioLauncher).Assembly.Location)!;
        var environment = StudioEnvironment.Load(pluginDirectory);
        var journal = SessionJournal.CreateUnder(environment.OutputRoot);
        var version = typeof(StudioLauncher).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var codex = new CodexService(new CodexOptions(environment.CodexHome, environment.CodexPath, environment.CodexWorkingDirectory, version)) { Journal = journal };
        var host = new RevitStudioHost(application, journal, environment.FurnitureTemplate);
        var session = new StudioSession(codex, host, journal);
        var model = new StudioViewModel(codex, session, environment, version);

        var window = new StudioWindow(model);
        new WindowInteropHelper(window) { Owner = application.MainWindowHandle };
        window.Closed += (_, _) =>
        {
            _window = null;
            model.Dispose();
            session.Cancel();
            codex.Dispose(); // stops both app-server processes now; nothing needs them once the window is gone
            if (_revitClosing)
            {
                // Revit runs no more external events once it is shutting down, so release the host
                // right here, in OnShutdown's API context, instead of queueing the release.
                session.Dispose();
                host.Dispose();
            }
            else _ = session.StopAsync(); // cancels, lets queued Revit work settle, then releases the host
        };
        _window = window;
        window.Show();
        // Start inside a dispatcher operation, so every await in the view model resumes on the UI
        // thread. Revit's command context does not always carry WPF's synchronization context.
        window.Dispatcher.BeginInvoke(new Action(() => _ = model.StartAsync()));
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
