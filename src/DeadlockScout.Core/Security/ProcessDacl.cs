namespace DeadlockScout.Core.Security;

/// <summary>
/// The access list DeadlockScout puts on its own process at start-up: other programs of the same Windows user may see
/// that it runs, wait for it and end it (Task Manager, the installer), but not read or write its memory, start threads
/// in it, copy its handles or change this list. That keeps the deadlock-api key in memory, and the app's code, out of
/// reach of other unelevated programs. The "owner rights" entry matters: without it the process's owner (the user)
/// could always rewrite the list and then open it for anything. An administrator with the debug privilege still can,
/// as for every process. Pure, so tested; <c>App/Interop/ProcessHardening</c> applies it.
/// </summary>
public static class ProcessDacl
{
    public const uint Terminate = 0x0001;
    public const uint CreateThread = 0x0002;
    public const uint VmOperation = 0x0008;
    public const uint VmRead = 0x0010;
    public const uint VmWrite = 0x0020;
    public const uint DupHandle = 0x0040;
    public const uint CreateProcess = 0x0080;
    public const uint SetQuota = 0x0100;
    public const uint SetInformation = 0x0200;
    public const uint QueryInformation = 0x0400;
    public const uint SuspendResume = 0x0800;
    public const uint QueryLimitedInformation = 0x1000;
    public const uint Delete = 0x0001_0000;
    public const uint ReadControl = 0x0002_0000;
    public const uint WriteDac = 0x0004_0000;
    public const uint WriteOwner = 0x0008_0000;
    public const uint Synchronize = 0x0010_0000;

    /// <summary>What everyone but SYSTEM gets: see it, query it, wait for it, end it.</summary>
    public const uint Allowed = Terminate | QueryInformation | QueryLimitedInformation | ReadControl | Synchronize;

    /// <summary>What nobody but SYSTEM gets any more.</summary>
    public const uint Refused = CreateThread | VmOperation | VmRead | VmWrite | DupHandle | CreateProcess | SetQuota | SetInformation
                                | SuspendResume | Delete | WriteDac | WriteOwner;

    /// <summary>
    /// The access list as SDDL: protected (nothing inherited), SYSTEM full, and <see cref="Allowed"/> for the owner's
    /// rights, the administrators and <paramref name="userSid"/> (the account the app runs as).
    /// </summary>
    public static string Sddl(string? userSid)
    {
        string allowed = $"0x{Allowed:x}";
        var sddl = $"D:P(A;;GA;;;SY)(A;;{allowed};;;OW)(A;;{allowed};;;BA)";
        if (!string.IsNullOrWhiteSpace(userSid) && userSid.StartsWith("S-1-", StringComparison.Ordinal)) sddl += $"(A;;{allowed};;;{userSid})";
        return sddl;
    }
}
