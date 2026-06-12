using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ObeliskLauncher;

public static class ProcessCommandLineReader
{
    private const int ProcessBasicInformation = 0;
    private const int ProcessQueryInformation = 0x0400;
    private const int ProcessVmRead = 0x0010;

    public static string? TryRead(Process process)
    {
        var handle = IntPtr.Zero;

        try
        {
            handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, process.Id);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var basicInfo = new ProcessBasicInformationData();
            var status = NtQueryInformationProcess(
                handle,
                ProcessBasicInformation,
                ref basicInfo,
                Marshal.SizeOf<ProcessBasicInformationData>(),
                out _);

            if (status != 0 || basicInfo.PebBaseAddress == IntPtr.Zero)
            {
                return null;
            }

            var processParametersAddress = ReadPointer(handle, basicInfo.PebBaseAddress + (IntPtr.Size == 8 ? 0x20 : 0x10));
            if (processParametersAddress == IntPtr.Zero)
            {
                return null;
            }

            var commandLineAddress = processParametersAddress + (IntPtr.Size == 8 ? 0x70 : 0x40);
            return ReadUnicodeString(handle, commandLineAddress);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                _ = CloseHandle(handle);
            }
        }
    }

    private static IntPtr ReadPointer(IntPtr processHandle, IntPtr address)
    {
        var buffer = new byte[IntPtr.Size];
        if (!ReadProcessBytes(processHandle, address, buffer))
        {
            return IntPtr.Zero;
        }

        return IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(buffer, 0))
            : new IntPtr(BitConverter.ToInt32(buffer, 0));
    }

    private static string? ReadUnicodeString(IntPtr processHandle, IntPtr address)
    {
        var header = new byte[IntPtr.Size == 8 ? 16 : 8];
        if (!ReadProcessBytes(processHandle, address, header))
        {
            return null;
        }

        var length = BitConverter.ToUInt16(header, 0);
        if (length == 0 || length > 32_766)
        {
            return null;
        }

        var bufferAddress = IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(header, 8))
            : new IntPtr(BitConverter.ToInt32(header, 4));

        if (bufferAddress == IntPtr.Zero)
        {
            return null;
        }

        var textBuffer = new byte[length];
        return ReadProcessBytes(processHandle, bufferAddress, textBuffer)
            ? Encoding.Unicode.GetString(textBuffer).TrimEnd('\0')
            : null;
    }

    private static bool ReadProcessBytes(IntPtr processHandle, IntPtr address, byte[] buffer)
    {
        return ReadProcessMemory(processHandle, address, buffer, buffer.Length, out var bytesRead)
               && bytesRead.ToInt64() == buffer.Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformationData
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2A;
        public IntPtr Reserved2B;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformationData processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out IntPtr numberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
