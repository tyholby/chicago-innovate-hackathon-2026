using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace FamilyStudio.Revit;

/// <summary>Adds the Family Studio tab (Family Studio and View2Render) and follows Revit's light or dark UI theme.</summary>
public sealed class App : IExternalApplication
{
    public const string TabName = "Family Studio";

    /// <summary>Raised when Revit's UI theme changes (not the canvas theme), with true when it is now dark.</summary>
    internal static event Action<bool>? UiThemeChanged;

    public Result OnStartup(UIControlledApplication application)
    {
        try { application.CreateRibbonTab(TabName); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* the tab already exists */ }

        var panel = application.CreateRibbonPanel(TabName, "Design");
        var button = new PushButtonData("FamilyStudioOpen", "Family\nStudio", typeof(App).Assembly.Location, typeof(OpenStudioCommand).FullName)
        {
            ToolTip = "Turn a description or a product photo into a native Revit furniture family.",
            LongDescription = "Describe an item or drop in a photo, check its dimensions and finishes, and Family Studio " +
                              "builds a native Furniture family you can load into any open project. Signs in with ChatGPT.",
            Image = RibbonIcon.Render(16),
            LargeImage = RibbonIcon.Render(32)
        };
        panel.AddItem(button);

        var render = application.CreateRibbonPanel(TabName, "Render");
        render.AddItem(new PushButtonData("FamilyStudioView2Render", "View2Render", typeof(App).Assembly.Location, typeof(ViewToRenderCommand).FullName)
        {
            ToolTip = "Render the active view as a photorealistic image with ChatGPT.",
            LongDescription = "Captures what the active view shows. Add an optional prompt and reference images, then ChatGPT image " +
                              "generation renders it photorealistically, and you can download the result. Signs in with ChatGPT.",
            Image = RibbonIcon.RenderView(16),
            LargeImage = RibbonIcon.RenderView(32),
            AvailabilityClassName = typeof(ViewToRenderAvailability).FullName
        });
        application.ThemeChanged += OnThemeChanged;
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.ThemeChanged -= OnThemeChanged;
        StudioLauncher.Shutdown();
        RenderLauncher.Shutdown();
        return Result.Succeeded;
    }

    private static void OnThemeChanged(object? sender, ThemeChangedEventArgs args)
    {
        // Read the theme here, inside Revit's event: listeners apply it later, outside the API context.
        if (args.ThemeChangedType == ThemeType.UITheme) UiThemeChanged?.Invoke(UI.ThemeManager.RevitIsDark);
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class OpenStudioCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            StudioLauncher.Open(commandData.Application);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}

/// <summary>View2Render: captures the active view and opens (or brings forward) the render window.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class ViewToRenderCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            RenderLauncher.Open(commandData.Application);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}

/// <summary>View2Render needs an open document with a view to capture.</summary>
public sealed class ViewToRenderAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => applicationData.ActiveUIDocument is not null;
}
