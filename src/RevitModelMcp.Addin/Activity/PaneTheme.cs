using Autodesk.Revit.UI;

namespace RevitModelMcp.Activity;

/// <summary>
/// Tracks Revit's UI theme (light/dark) for the activity pane. Revit 2024+ exposes
/// <see cref="UIThemeManager.CurrentTheme"/> and raises <see cref="UIControlledApplication.ThemeChanged"/>
/// when the user switches it; earlier Revit versions have no dark theme, so the pane always renders light.
/// </summary>
internal static class PaneTheme
{
#if !REVIT2024_OR_GREATER
#pragma warning disable CS0067 // never raised pre-Revit-2024: no ThemeChanged event exists to forward.
#endif
    /// <summary>Raised after Revit's UI theme changes. Handlers run on the Revit UI thread.</summary>
    public static event Action? Changed;
#if !REVIT2024_OR_GREATER
#pragma warning restore CS0067
#endif

    public static bool IsDark
    {
        get
        {
#if REVIT2024_OR_GREATER
            return UIThemeManager.CurrentTheme == UITheme.Dark;
#else
            return false;
#endif
        }
    }

    /// <summary>Subscribes to Revit's theme-change notification during application startup.</summary>
    public static void Register(UIControlledApplication application)
    {
#if REVIT2024_OR_GREATER
        application.ThemeChanged += (_, _) => Changed?.Invoke();
#endif
    }
}
