using System.Runtime.InteropServices;
using System.Security.Principal;
using DeadlockScout.Core.Security;
using Microsoft.Extensions.Logging;

namespace DeadlockScout.App.Interop;

/// <summary>
/// Locks the app's own process down at start-up (0.3, "Ready for 1.0"). Nothing here touches the game: it is the app
/// protecting itself from other programs.
/// <list type="bullet">
/// <item>DLLs load from the app's folder and System32 only, never the current folder or PATH;</item>
/// <item>Windows' image-load policy: no DLLs from network shares or with a low integrity label, and a System32 copy of a
/// DLL before any other;</item>
/// <item>no legacy extension points in the process: AppInit DLLs, global window hooks and Winsock layered providers of
/// other programs are not loaded into it;</item>
/// <item>the process's access list (<see cref="ProcessDacl"/>): no other program reads or writes its memory or starts a
/// thread in it.</item>
/// </list>
/// Each step stands alone: one that fails is logged and the rest still apply. <c>security.hardenProcess</c> turns all of
/// it off (from the next start), for a tool that has to attach to the app.
/// </summary>
public static class ProcessHardening
{
    /// <summary>What was applied, for Diagnostics: "on: DLL search, image loads, extension points, access list".</summary>
    public static string Status { get; private set; } = "not applied";

    public static void Apply(bool enabled, ILogger log)
    {
        if (!OperatingSystem.IsWindows()) { Status = "not on Windows"; return; }
        if (!enabled) { Status = "off (security.hardenProcess)"; log.LogInformation("Process hardening is off in settings"); return; }
        var done = new List<string>();
        var failed = new List<string>();
        void Step(string name, Func<int> apply)
        {
            int error;
            try { error = apply(); }
            catch (Exception ex) { error = -1; log.LogWarning(ex, "Process hardening: {Step} threw", name); }
            if (error == 0) done.Add(name);
            else { failed.Add(name); log.LogWarning("Process hardening: {Step} could not be applied (error {Error})", name, error); }
        }

        Step("DLL search", () => SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs) ? 0 : Marshal.GetLastWin32Error());
        Step("image loads", () =>
        {
            var policy = ImageLoadNoRemote | ImageLoadNoLowLabel | ImageLoadPreferSystem32;
            return SetProcessMitigationPolicy(ProcessImageLoadPolicy, ref policy, sizeof(uint)) ? 0 : Marshal.GetLastWin32Error();
        });
        Step("extension points", () =>
        {
            uint policy = 1; // DisableExtensionPoints
            return SetProcessMitigationPolicy(ProcessExtensionPointDisablePolicy, ref policy, sizeof(uint)) ? 0 : Marshal.GetLastWin32Error();
        });
        Step("access list", ApplyDacl);

        Status = failed.Count == 0 ? $"on: {string.Join(", ", done)}"
            : done.Count == 0 ? $"failed: {string.Join(", ", failed)} (see the log)"
            : $"on: {string.Join(", ", done)}; failed: {string.Join(", ", failed)} (see the log)";
        log.LogInformation("Process hardening {Status}", Status);
    }

    private static int ApplyDacl()
    {
        string? sid;
        using (var me = WindowsIdentity.GetCurrent()) sid = me.User?.Value;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(ProcessDacl.Sddl(sid), SddlRevision1, out var sd, out _))
            return Marshal.GetLastWin32Error();
        try
        {
            if (!GetSecurityDescriptorDacl(sd, out bool present, out var dacl, out _) || !present) return Marshal.GetLastWin32Error() is var e and not 0 ? e : -2;
            return (int)SetSecurityInfo(GetCurrentProcess(), SeKernelObject, DaclSecurityInformation | ProtectedDaclSecurityInformation,
                IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
        }
        finally { LocalFree(sd); }
    }

    private const uint LoadLibrarySearchDefaultDirs = 0x0000_1000;
    private const int ProcessExtensionPointDisablePolicy = 6;
    private const int ProcessImageLoadPolicy = 10;
    private const uint ImageLoadNoRemote = 0x1, ImageLoadNoLowLabel = 0x2, ImageLoadPreferSystem32 = 0x4;
    private const uint SddlRevision1 = 1;
    private const int SeKernelObject = 6;
    private const uint DaclSecurityInformation = 0x4, ProtectedDaclSecurityInformation = 0x8000_0000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref uint buffer, nuint length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, out bool present, out IntPtr dacl, out bool defaulted);

    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(IntPtr handle, int objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
}
