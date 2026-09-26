using System.ComponentModel;
using System.Windows;
using FamilyStudio.Core.Pipeline;

namespace FamilyStudio.Revit.UI;

public partial class StudioWindow : Window
{
    private readonly StudioViewModel _model;
    private bool _forceClose;

    public StudioWindow(StudioViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        ThemeManager.Apply(this);
        App.UiThemeChanged += OnThemeChanged;
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        Closing += OnClosing;
        Closed += (_, _) => App.UiThemeChanged -= OnThemeChanged;
    }

    /// <summary>Brings the existing window forward when the ribbon button is pressed again.</summary>
    public void Reveal()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Closes without asking, for Revit shutdown.</summary>
    public void CloseForShutdown()
    {
        _forceClose = true;
        Close();
    }

    private void OnThemeChanged(bool dark) => Dispatcher.BeginInvoke(new Action(() => ThemeManager.Apply(this, dark)));

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_forceClose || !_model.IsBusy) return;
        var answer = MessageBox.Show(this,
            "Family Studio is still working. Close it and stop the current step? Everything already built is kept.",
            "Family Studio", MessageBoxButton.OKCancel, MessageBoxImage.None, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) e.Cancel = true;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnShowRegister(object sender, RoutedEventArgs e)
    {
        Register.PlacementTarget = (UIElement)sender;
        Register.IsOpen = true;
    }

    private void OnPhotoDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _model.CanEditBrief && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPhotoDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        if (files.Length > 1)
        {
            _model.Alert = "Drop one photo of one item.";
            return;
        }
        _model.SetPhoto(files[0]);
    }
}
