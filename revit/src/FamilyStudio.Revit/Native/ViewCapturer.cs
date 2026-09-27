using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using FamilyStudio.Core.Pipeline;

namespace FamilyStudio.Revit.Native;

/// <summary>Exports what the active Revit view shows, for View2Render. Runs only in Revit's API context.</summary>
internal static class ViewCapturer
{
    /// <summary>Wide enough for the image model to read the detail. The height follows the view's proportions.</summary>
    private const int PixelWidth = 2048;

    public static ViewCapture Capture(UIApplication application, SessionJournal journal, int number)
    {
        var active = application.ActiveUIDocument ?? throw new InvalidOperationException("Open a project and a view, then capture again.");
        var document = active.Document;
        var view = active.ActiveView ?? throw new InvalidOperationException("Open a view, then capture again.");
        if (!CanRender(view))
            throw new InvalidOperationException(
                $"View2Render renders 3D views, plans, sections and elevations, and \"{view.Name}\" is a {Kind(view).ToLowerInvariant()}. Open a model view, then capture again.");

        // A fresh folder per capture, because Revit may add the view's name to the file name.
        var folder = Path.GetDirectoryName(journal.PathFor("captures", number.ToString("D2"), "view"))!;
        var options = new ImageExportOptions
        {
            ExportRange = ExportRange.VisibleRegionOfCurrentView,
            FilePath = Path.Combine(folder, "view"),
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            ImageResolution = ImageResolution.DPI_150,
            PixelSize = PixelWidth,
            FitDirection = FitDirectionType.Horizontal,
            ZoomType = ZoomFitType.FitToPage
        };
        document.ExportImage(options);
        var file = Directory.GetFiles(folder, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            ?? throw new InvalidOperationException("Revit did not export the view. Try again.");
        var size = ReferenceImages.Measure(File.ReadAllBytes(file)) ?? throw new InvalidOperationException("Revit exported an unreadable image. Try again.");
        journal.Write("view_captured", new { number, view = view.Name, kind = view.ViewType.ToString(), size.Width, size.Height });
        return new ViewCapture(file, view.Name, Kind(view), document.Title, size.Width, size.Height);
    }

    private static bool CanRender(View view) => !view.IsTemplate && view.ViewType is ViewType.ThreeD or ViewType.FloorPlan or ViewType.CeilingPlan
        or ViewType.EngineeringPlan or ViewType.AreaPlan or ViewType.Elevation or ViewType.Section or ViewType.Detail or ViewType.Walkthrough
        or ViewType.Rendering;

    private static string Kind(View view) => view.ViewType switch
    {
        ViewType.ThreeD => "3D view",
        ViewType.FloorPlan => "Floor plan",
        ViewType.CeilingPlan => "Ceiling plan",
        ViewType.EngineeringPlan => "Structural plan",
        ViewType.AreaPlan => "Area plan",
        ViewType.Elevation => "Elevation",
        ViewType.Section => "Section",
        ViewType.Detail => "Detail view",
        ViewType.Walkthrough => "Walkthrough",
        ViewType.Rendering => "Rendering",
        ViewType.DrawingSheet => "Sheet",
        ViewType.Schedule or ViewType.ColumnSchedule or ViewType.PanelSchedule => "Schedule",
        ViewType.Legend => "Legend",
        ViewType.DraftingView => "Drafting view",
        _ => "View"
    };
}
