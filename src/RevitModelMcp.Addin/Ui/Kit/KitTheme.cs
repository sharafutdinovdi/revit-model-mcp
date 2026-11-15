using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace RevitModelMcp.Ui.Kit;

internal static class KitTheme
{
    private static readonly ConditionalWeakTable<Window, object> Windows = new();

    internal static void Apply(Window window)
    {
        if (Windows.TryGetValue(window, out _)) return;
        Windows.Add(window, new object());
        foreach (var name in new[] { "KitTokens.xaml", "KitIcons.xaml", "KitStyles.xaml" })
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/RevitModelMcp;component/Ui/Kit/" + name, UriKind.Relative) });
        window.SetResourceReference(Window.BackgroundProperty, "Surface");
        window.SetResourceReference(Window.ForegroundProperty, "Text");
        window.SetResourceReference(Window.FontFamilyProperty, "KitFont");
        window.FontSize = 12.5;
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
    }
}
