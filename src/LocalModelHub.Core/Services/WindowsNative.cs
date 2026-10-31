using System.Runtime.InteropServices;

namespace LocalModelHub.Core.Services;

internal static class WindowsNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll")]
    private static extern bool IsProcessorFeaturePresent(uint feature);

    private const uint PfAvx2InstructionsAvailable = 40;
    private const uint PfAvx512FInstructionsAvailable = 41;

    public static (long Total, long Available) GetMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
        return ((long)status.TotalPhys, (long)status.AvailPhys);
    }

    public static bool HasAvx2 => IsProcessorFeaturePresent(PfAvx2InstructionsAvailable);
    public static bool HasAvx512 => IsProcessorFeaturePresent(PfAvx512FInstructionsAvailable);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct lpFileOp);

    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    /// <summary>
    /// 把文件移入系统回收站。非 Windows、文件不存在或回收站不可用时返回 false，
    /// 由调用方退化为永久删除并据此给出可诊断的提示。
    /// </summary>
    public static bool TryDeleteToRecycleBin(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return false;

        var from = Marshal.StringToHGlobalUni(path + "\0");
        try
        {
            var operation = new ShFileOpStruct
            {
                wFunc = FoDelete,
                pFrom = from,
                fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi
            };
            var code = SHFileOperationW(ref operation);
            return code == 0 && operation.fAnyOperationsAborted == 0 && !File.Exists(path);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 回收站 API 缺失（精简版系统/非桌面会话）：交给调用方永久删除
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }
}
