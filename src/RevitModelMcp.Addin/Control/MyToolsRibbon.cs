using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JetBrains.Annotations;
using Nice3point.Revit.Extensions;
using Nice3point.Revit.Toolkit.External;
using RevitModelMcp.Activity;
using RevitModelMcp.Core.Control;

namespace RevitModelMcp.Control;

internal static class MyToolsRibbon
{
    private static readonly List<PushButton> Buttons = [];
    private static IReadOnlyList<string> _names = [];

    internal static void Initialize(PulldownButton menu)
    {
        Buttons.Add(menu.AddPushButton<MyToolSlot01>("Saved tool 1"));
        Buttons.Add(menu.AddPushButton<MyToolSlot02>("Saved tool 2"));
        Buttons.Add(menu.AddPushButton<MyToolSlot03>("Saved tool 3"));
        Buttons.Add(menu.AddPushButton<MyToolSlot04>("Saved tool 4"));
        Buttons.Add(menu.AddPushButton<MyToolSlot05>("Saved tool 5"));
        Buttons.Add(menu.AddPushButton<MyToolSlot06>("Saved tool 6"));
        Buttons.Add(menu.AddPushButton<MyToolSlot07>("Saved tool 7"));
        Buttons.Add(menu.AddPushButton<MyToolSlot08>("Saved tool 8"));
        Buttons.Add(menu.AddPushButton<MyToolSlot09>("Saved tool 9"));
        Buttons.Add(menu.AddPushButton<MyToolSlot10>("Saved tool 10"));
        Buttons.Add(menu.AddPushButton<MyToolSlot11>("Saved tool 11"));
        Buttons.Add(menu.AddPushButton<MyToolSlot12>("Saved tool 12"));
        Buttons.Add(menu.AddPushButton<MyToolSlot13>("Saved tool 13"));
        Buttons.Add(menu.AddPushButton<MyToolSlot14>("Saved tool 14"));
        Buttons.Add(menu.AddPushButton<MyToolSlot15>("Saved tool 15"));
        Buttons.Add(menu.AddPushButton<MyToolSlot16>("Saved tool 16"));
        Buttons.Add(menu.AddPushButton<MyToolSlot17>("Saved tool 17"));
        Buttons.Add(menu.AddPushButton<MyToolSlot18>("Saved tool 18"));
        Buttons.Add(menu.AddPushButton<MyToolSlot19>("Saved tool 19"));
        Buttons.Add(menu.AddPushButton<MyToolSlot20>("Saved tool 20"));
        Buttons.Add(menu.AddPushButton<MyToolSlot21>("Saved tool 21"));
        Buttons.Add(menu.AddPushButton<MyToolSlot22>("Saved tool 22"));
        Buttons.Add(menu.AddPushButton<MyToolSlot23>("Saved tool 23"));
        Buttons.Add(menu.AddPushButton<MyToolSlot24>("Saved tool 24"));
        foreach (var button in Buttons)
            button.SetAvailabilityController<SavedToolAvailability>();
        menu.AddSeparator();
        menu.AddPushButton<MyToolsCommand>("Settings").SetAvailabilityController<ZeroDocumentAvailability>();
        Refresh();
        PaneTheme.Changed += Refresh;
    }

    internal static void Dispose()
    {
        PaneTheme.Changed -= Refresh;
        Buttons.Clear();
        _names = [];
    }

    internal static void Refresh()
    {
        if (Buttons.Count == 0) return;
        try
        {
            var tools = MyTools.Store.List();
            _names = ToolRibbonSlots.Assign(tools.Select(tool => tool.Name), MyTools.Presentation.Get);
            for (var index = 0; index < Buttons.Count; index++)
            {
                var button = Buttons[index];
                button.Visible = index < _names.Count;
                if (!button.Visible) continue;
                var tool = tools.Single(candidate => candidate.Name == _names[index]);
                var presentation = MyTools.Presentation.Get(tool.Name);
                button.ItemText = presentation.Label;
                button.ToolTip = tool.Description;
                button.LongDescription = $"{tool.Mode}; revision {tool.Revision[..12]}; tested Revit years: {string.Join(", ", tool.TestedRevitYears)}. Saved C# has full Revit API and user rights.";
                button.Image = ToolIcons.Load(presentation, 16);
                button.LargeImage = ToolIcons.Load(presentation, 32);
            }
        }
        catch (Exception)
        {
            _names = [];
            foreach (var button in Buttons) button.Visible = false;
            PluginLog.Warn("My tools ribbon could not be refreshed. Open Settings to review the store.");
        }
    }

    internal static void Run(UIApplication application, int slot)
    {
        if (slot < _names.Count) RibbonToolRunner.Run(application, _names[slot]);
    }
}

[UsedImplicitly]
public sealed class SavedToolAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => applicationData.ActiveUIDocument is not null;
}

internal static class ToolIcons
{
    internal static ImageSource Load(ToolPresentation presentation, int size)
    {
        var png = size == 16 ? presentation.SmallPng : presentation.LargePng;
        if (!string.IsNullOrEmpty(png))
        {
            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(png));
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                if (frame.PixelWidth != size || frame.PixelHeight != size) throw new ArgumentException("Stored PNG dimensions are invalid.");
                var image = new WriteableBitmap(frame);
                image.Freeze();
                return image;
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException or FileFormatException)
            {
                // Presentation files are not approved code. A damaged icon uses the default fallback.
            }
        }
        var suffix = PaneTheme.IsDark ? "-dark" : "";
        var fallback = new BitmapImage(new Uri($"pack://application:,,,/RevitModelMcp;component/Resources/Icons/Script{size}{suffix}.png"));
        fallback.Freeze();
        return fallback;
    }

    internal static ToolPresentation Import(string path, ToolPresentation presentation)
    {
        if (new FileInfo(path).Length > 256 * 1024) throw new ArgumentException("Choose a PNG no larger than 256 KB.");
        using var stream = File.OpenRead(path);
        BitmapDecoder decoder;
        try { decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None); }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            throw new ArgumentException("The file cannot be decoded as a PNG.");
        }
        if (decoder is not PngBitmapDecoder || decoder.Frames.Count != 1) throw new ArgumentException("Choose a single-frame PNG image.");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth is < 1 or > 1024 || frame.PixelHeight is < 1 or > 1024)
            throw new ArgumentException("PNG dimensions must be between 1 and 1024 pixels.");
        string Resize(int size)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                var scale = Math.Min((double)size / frame.PixelWidth, (double)size / frame.PixelHeight);
                var width = frame.PixelWidth * scale;
                var height = frame.PixelHeight * scale;
                drawing.DrawImage(frame, new System.Windows.Rect((size - width) / 2, (size - height) / 2, width, height));
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            encoder.Save(output);
            return Convert.ToBase64String(output.ToArray());
        }
        return presentation with
        {
            SmallPng = Resize(16),
            LargePng = Resize(32)
        };
    }
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot01 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 0);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot02 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 1);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot03 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 2);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot04 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 3);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot05 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 4);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot06 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 5);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot07 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 6);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot08 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 7);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot09 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 8);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot10 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 9);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot11 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 10);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot12 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 11);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot13 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 12);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot14 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 13);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot15 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 14);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot16 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 15);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot17 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 16);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot18 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 17);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot19 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 18);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot20 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 19);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot21 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 20);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot22 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 21);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot23 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 22);
}

[Transaction(TransactionMode.Manual)]
[UsedImplicitly]
public sealed class MyToolSlot24 : ExternalCommand
{
    public override void Execute() => MyToolsRibbon.Run(Application, 23);
}
