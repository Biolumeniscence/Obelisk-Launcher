using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ObeliskLauncher;

public sealed class WindowsTitleProbe
{
    public string? FindTitleForServer(Process? process, ServerMapInstance instance)
    {
        var title = TryReadProcessMainWindowTitle(process);
        if (LooksLikeAsaServerTitle(title, process?.Id, instance))
        {
            return title;
        }

        return EnumerateVisibleWindowTitles()
            .Where(candidate => LooksLikeAsaServerTitle(candidate, process?.Id, instance))
            .OrderByDescending(candidate => process is not null && candidate.Contains($"Process {process.Id}", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private static string? TryReadProcessMainWindowTitle(Process? process)
    {
        if (process is null)
        {
            return null;
        }

        try
        {
            process.Refresh();
            return string.IsNullOrWhiteSpace(process.MainWindowTitle) ? null : process.MainWindowTitle;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeAsaServerTitle(string? title, int? processId, ServerMapInstance instance)
    {
        if (string.IsNullOrWhiteSpace(title) || !title.Contains("Players:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (processId is not null && title.Contains($"Process {processId}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return title.Contains(instance.SessionName, StringComparison.OrdinalIgnoreCase)
               && title.Contains(instance.MapCode, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateVisibleWindowTitles()
    {
        var titles = new List<string>();

        EnumWindows((handle, _) =>
        {
            try
            {
                if (!IsWindowVisible(handle))
                {
                    return true;
                }

                var length = GetWindowTextLength(handle);
                if (length <= 0)
                {
                    return true;
                }

                var builder = new StringBuilder(length + 1);
                _ = GetWindowText(handle, builder, builder.Capacity);
                var title = builder.ToString();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    titles.Add(title);
                }
            }
            catch
            {
                // Window enumeration must not destabilize polling.
            }

            return true;
        }, IntPtr.Zero);

        return titles;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}
