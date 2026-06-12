using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace ObeliskLauncher;

public sealed record AsaServerConsoleStatus(
    bool IsReady,
    string? StatusText,
    string? Uptime);

public sealed class AsaServerConsoleProbe
{
    private const int WmGetText = 0x000D;
    private const int WmGetTextLength = 0x000E;

    public AsaServerConsoleStatus? TryRead(Process? process, ServerMapInstance instance)
    {
        var handle = FindConsoleWindow(process, instance);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var childTexts = EnumerateChildTexts(handle)
            .Concat(EnumerateAutomationTexts(handle))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var statusIndex = childTexts.FindIndex(text => text.Equals("Status", StringComparison.OrdinalIgnoreCase));
        var statusText = statusIndex >= 0 && statusIndex + 1 < childTexts.Count
            ? childTexts[statusIndex + 1]
            : childTexts.FirstOrDefault(text => text.Contains("Ready", StringComparison.OrdinalIgnoreCase));

        var timeIndex = childTexts.FindIndex(text => text.Equals("Time", StringComparison.OrdinalIgnoreCase));
        var uptime = timeIndex >= 0 && timeIndex + 1 < childTexts.Count ? childTexts[timeIndex + 1] : null;
        var isReady = statusText?.Contains("Ready", StringComparison.OrdinalIgnoreCase) == true;
        return new AsaServerConsoleStatus(isReady, statusText, uptime);
    }

    private static IntPtr FindConsoleWindow(Process? process, ServerMapInstance instance)
    {
        if (process is not null)
        {
            try
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return process.MainWindowHandle;
                }
            }
            catch
            {
                // Fall through to full window enumeration.
            }
        }

        var result = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            var className = GetClassNameText(handle);
            if (!className.Equals("FConsoleWindow", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var title = GetWindowTextValue(handle);
            if (!title.Contains("Server Console", StringComparison.OrdinalIgnoreCase)
                || !title.Contains("ArkAscendedServer", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (process is not null)
            {
                _ = GetWindowThreadProcessId(handle, out var windowProcessId);
                if (windowProcessId == process.Id)
                {
                    result = handle;
                    return false;
                }
            }

            if (title.Contains(instance.SessionName, StringComparison.OrdinalIgnoreCase)
                || title.Contains(instance.MapCode, StringComparison.OrdinalIgnoreCase))
            {
                result = handle;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static IEnumerable<string> EnumerateChildTexts(IntPtr parent)
    {
        var texts = new List<string>();
        EnumChildWindows(parent, (handle, _) =>
        {
            var text = GetWindowTextValue(handle);
            if (!string.IsNullOrWhiteSpace(text))
            {
                texts.Add(text.Trim());
            }

            return true;
        }, IntPtr.Zero);

        return texts;
    }

    private static IEnumerable<string> EnumerateAutomationTexts(IntPtr parent)
    {
        var texts = new List<string>();

        try
        {
            var root = AutomationElement.FromHandle(parent);
            if (root is null)
            {
                return texts;
            }

            WalkAutomationTree(root, texts, 0);
        }
        catch
        {
            // UI Automation is best-effort only. WinAPI child controls remain the stable path.
        }

        return texts;
    }

    private static void WalkAutomationTree(AutomationElement element, List<string> texts, int depth)
    {
        if (depth > 6 || texts.Count > 300)
        {
            return;
        }

        try
        {
            var name = element.Current.Name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                texts.Add(name.Trim());
            }
        }
        catch
        {
            return;
        }

        try
        {
            var walker = TreeWalker.ControlViewWalker;
            for (var child = walker.GetFirstChild(element);
                 child is not null && texts.Count <= 300;
                 child = walker.GetNextSibling(child))
            {
                WalkAutomationTree(child, texts, depth + 1);
            }
        }
        catch
        {
            // Some custom controls reject automation traversal. Keep whatever was already read.
        }
    }

    private static string GetClassNameText(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        _ = GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetWindowTextValue(IntPtr handle)
    {
        var length = Math.Max(GetWindowTextLength(handle), (int)SendMessage(handle, WmGetTextLength, IntPtr.Zero, IntPtr.Zero));
        length = Math.Max(length, 512);

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);

        if (builder.Length == 0)
        {
            _ = SendMessage(handle, WmGetText, new IntPtr(builder.Capacity), builder);
        }

        return builder.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
