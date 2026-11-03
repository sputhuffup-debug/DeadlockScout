using DeadlockScout.Core.Security;

namespace DeadlockScout.Tests.Core;

public class ProcessDaclTests
{
    [Fact]
    public void Other_programs_may_see_wait_for_and_end_the_app_but_never_touch_its_memory()
    {
        Assert.Equal(0u, ProcessDacl.Allowed & ProcessDacl.Refused);
        foreach (var right in new[] { ProcessDacl.VmRead, ProcessDacl.VmWrite, ProcessDacl.VmOperation, ProcessDacl.CreateThread,
                                      ProcessDacl.DupHandle, ProcessDacl.SuspendResume, ProcessDacl.WriteDac, ProcessDacl.WriteOwner })
            Assert.Equal(0u, ProcessDacl.Allowed & right);
        foreach (var right in new[] { ProcessDacl.Terminate, ProcessDacl.Synchronize, ProcessDacl.QueryLimitedInformation })
            Assert.Equal(right, ProcessDacl.Allowed & right);
    }

    [Fact]
    public void The_list_is_protected_limits_the_owner_and_names_the_user()
    {
        var sddl = ProcessDacl.Sddl("S-1-5-21-1-2-3-1001");
        Assert.StartsWith("D:P(A;;GA;;;SY)", sddl); // protected, SYSTEM full
        Assert.Contains("(A;;0x121401;;;OW)", sddl); // owner rights: no implicit WRITE_DAC to undo it
        Assert.Contains("(A;;0x121401;;;BA)", sddl);
        Assert.EndsWith("(A;;0x121401;;;S-1-5-21-1-2-3-1001)", sddl);
        Assert.DoesNotContain("S-1-", ProcessDacl.Sddl(null));
        Assert.DoesNotContain("evil", ProcessDacl.Sddl(";;;evil)"));
    }
}
