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
}
