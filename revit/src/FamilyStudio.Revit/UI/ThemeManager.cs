using System.Windows;
using Autodesk.Revit.UI;

namespace FamilyStudio.Revit.UI;

/// <summary>Follows Revit's UI theme: Paper when Revit is light, Night drafting when it is dark.</summary>
internal static class ThemeManager
{
    private const string Paper = "/FamilyStudio.Revit;component/UI/Theme/Paper.xaml";
    private const string Night = "/FamilyStudio.Revit;component/UI/Theme/Night.xaml";

    public static bool RevitIsDark
    {
        get
        {
            try { return UIThemeManager.CurrentTheme == UITheme.Dark; }
            catch (Exception) { return false; }
        }
    }

    public static void Apply(FrameworkElement root) => Apply(root, RevitIsDark);

    public static void Apply(FrameworkElement root, bool dark)
    {
        var dictionaries = root.Resources.MergedDictionaries;
        var theme = (ResourceDictionary)Application.LoadComponent(new Uri(dark ? Night : Paper, UriKind.Relative));
        if (dictionaries.Count > 0) dictionaries[0] = theme;
        else dictionaries.Add(theme);
    }
}
