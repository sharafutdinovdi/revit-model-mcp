using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.Control;

internal static class NativeDialogReader
{
    private const uint WindowTextMessage = 0x000D;
    private const uint AbortIfHung = 0x0002;
    private const int WindowStyle = -16;

    internal static uint CurrentThreadId => GetCurrentThreadId();

    internal static HashSet<IntPtr> VisibleWindows(uint threadId)
    {
        var windows = new HashSet<IntPtr>();
        EnumThreadWindows(threadId, (window, _) =>
        {
            if (IsWindowVisible(window)) windows.Add(window);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    internal static IntPtr FindNewWindow(uint threadId, HashSet<IntPtr> existing)
    {
        using var process = Process.GetCurrentProcess();
        var currentProcessId = (uint)process.Id;
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && !existing.Contains(foreground) && IsWindowVisible(foreground) &&
            GetWindowThreadProcessId(foreground, out var processId) == threadId &&
            processId == currentProcessId)
            return foreground;
        IntPtr found = IntPtr.Zero;
        EnumThreadWindows(threadId, (window, _) =>
        {
            if (existing.Contains(window) || !IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out var processId);
            if (processId != currentProcessId) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    internal static IReadOnlyList<NativeDialogControl> Read(IntPtr dialog)
    {
        var controls = new List<NativeDialogControl>();
        EnumChildWindows(dialog, (window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            var className = new StringBuilder(128);
            GetClassName(window, className, className.Capacity);
            var name = className.ToString();
            var isButton = name.Equals("Button", StringComparison.OrdinalIgnoreCase);
            var kind = GetWindowLong(window, WindowStyle) & 0xF;
            var pushButton = isButton && kind is 0 or 1 or 0xE or 0xF;
            if (!pushButton && !name.Equals("Static", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("Edit", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) return true;
            var text = new StringBuilder(2048);
            if (isButton) GetWindowText(window, text, text.Capacity);
            else SendMessageTimeout(window, WindowTextMessage, (IntPtr)text.Capacity, text,
                AbortIfHung, 50, out _);
            controls.Add(new NativeDialogControl(name, text.ToString(), GetDlgCtrlID(window), pushButton));
            return true;
        }, IntPtr.Zero);
        return controls;
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, WindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr textLength,
        StringBuilder text, uint flags, uint timeoutMilliseconds, out IntPtr result);
}
