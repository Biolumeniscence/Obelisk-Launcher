using System.Runtime.InteropServices;
using System.Text;

namespace ObeliskLauncher;

public sealed class AttachedConsoleTitleReader
{
    private readonly object _syncRoot = new();

    public string? TryReadTitle(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        lock (_syncRoot)
        {
            try
            {
                FreeConsole();

                if (!AttachConsole((uint)processId))
                {
                    return null;
                }

                var builder = new StringBuilder(1024);
                var length = GetConsoleTitle(builder, builder.Capacity);
                return length <= 0 ? null : builder.ToString();
            }
            catch
            {
                return null;
            }
            finally
            {
                FreeConsole();
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetConsoleTitle(StringBuilder lpConsoleTitle, int nSize);
}
