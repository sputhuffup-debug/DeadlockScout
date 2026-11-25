using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DeadlockScout.App.Services;

/// <summary>
/// One full, compacting collection (the large object heap too) and the working set handed back to Windows (1.0.4).
/// The .NET collector lets a busy process's heap grow long before it collects: on Nov 23 the heap peaked at 150–250 MB
/// in a match while a forced collection afterwards found 20–55 MB alive, and each match started on the last one's
/// garbage. Run between matches (and when the main window goes to the tray), never during one.
/// </summary>
public static class MemoryTrim
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    /// <summary>Collects, trims and returns (working set before, after, .NET heap after) in MB.</summary>
    public static (long Before, long After, long Heap) Run()
    {
        long before;
        using (var self = Process.GetCurrentProcess()) before = self.WorkingSet64;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        if (OperatingSystem.IsWindows())
            using (var self = Process.GetCurrentProcess())
                try { SetProcessWorkingSetSize(self.Handle, -1, -1); } catch (Exception) { } // pages come back on use
        using var after = Process.GetCurrentProcess();
        const long mb = 1024 * 1024;
        return (before / mb, after.WorkingSet64 / mb, GC.GetTotalMemory(false) / mb);
    }

    public static void RunAndLog(ILogger log, string when)
    {
        var (before, after, heap) = Run();
        log.LogInformation("Memory {When}: {Before} MB -> {After} MB (.NET heap {Heap} MB)", when, before, after, heap);
    }
}
