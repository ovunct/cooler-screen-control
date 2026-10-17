using System.Windows;
using System.Windows.Media;
using Application = System.Windows.Application;
using SystemColors = System.Windows.SystemColors;

namespace CoolerScreenControl.Services;

/// <summary>
/// Light = the normal Windows look (no custom styles). Dark = the custom dark styles in Theme.xaml
/// plus a dark colour palette. The colours live in application resources under fixed keys so the
/// window can follow a change at runtime.
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _darkStyles;

    private static SolidColorBrush B(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    public static void Apply(bool dark)
    {
        var res = Application.Current.Resources;
        if (dark)
        {
            res["WindowBrush"] = B("#17181C");
            res["CardBrush"] = B("#212329");
            res["InputBrush"] = B("#2B2E36");
            res["HoverBrush"] = B("#363A45");
            res["PressedBrush"] = B("#444956");
            res["BorderBrushDark"] = B("#3A3E4A");
            res["TextBrush"] = B("#E8E9ED");
            res["MutedBrush"] = B("#9298A6");
            res["AccentBrush"] = B("#4C8DFF");
            res["AccentHoverBrush"] = B("#6AA1FF");
            res["SelectedRowBrush"] = B("#2C3A55");
            if (_darkStyles == null)
                _darkStyles = new ResourceDictionary { Source = new Uri("Theme.xaml", UriKind.Relative) };
            if (!res.MergedDictionaries.Contains(_darkStyles))
                res.MergedDictionaries.Add(_darkStyles);
        }
        else
        {
            if (_darkStyles != null) res.MergedDictionaries.Remove(_darkStyles);
            res["WindowBrush"] = SystemColors.WindowBrush;
            res["CardBrush"] = B("#F6F6F6");
            res["InputBrush"] = SystemColors.WindowBrush;
            res["HoverBrush"] = B("#E8E8E8");
            res["PressedBrush"] = B("#D8D8D8");
            res["BorderBrushDark"] = B("#CFCFCF");
            res["TextBrush"] = SystemColors.WindowTextBrush;
            res["MutedBrush"] = B("#6B6B6B");
            res["AccentBrush"] = B("#2F6FED");
            res["AccentHoverBrush"] = B("#4C86F5");
            res["SelectedRowBrush"] = B("#D9E6FF");
        }
    }
}
