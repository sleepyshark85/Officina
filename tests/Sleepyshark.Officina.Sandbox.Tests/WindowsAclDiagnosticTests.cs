using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using static Sleepyshark.Officina.Sandbox.Tests.RealSandbox;

namespace Sleepyshark.Officina.Sandbox.Tests;

// TEMPORARY: finds out which entries stop an AppContainer reading a file. Removed before review.
[SupportedOSPlatform("windows")]
public sealed class WindowsAclDiagnosticTests : IDisposable
{
    private readonly RealSandbox real = new(new WindowsSandbox());

    public static bool OnWindows => OperatingSystem.IsWindows();

    public void Dispose() => real.Dispose();

    [Fact(Skip = "Windows only", SkipUnless = nameof(OnWindows))]
    public async Task Diagnose()
    {
        await real.RunAsync("echo first");
        var container = new DirectoryInfo(real.WorkingCopy).GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(rule => (SecurityIdentifier)rule.IdentityReference).First(sid => sid.Value.StartsWith("S-1-15-2-", StringComparison.Ordinal));
        string Write(string name)
        {
            var path = Path.Combine(real.WorkingCopy, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"secret-{name}");
            return path;
        }

        var a = new FileInfo(Write("a.txt"));
        var sa = a.GetAccessControl();
        sa.AddAccessRule(new FileSystemAccessRule(container, FileSystemRights.FullControl, AccessControlType.Deny));
        a.SetAccessControl(sa);

        var b = new FileInfo(Write("b.txt"));
        var sb = b.GetAccessControl();
        sb.AddAccessRule(new FileSystemAccessRule(container, FileSystemRights.Read, AccessControlType.Deny));
        b.SetAccessControl(sb);

        var c = new FileInfo(Write("c.txt"));
        var sc = c.GetAccessControl();
        sc.SetAccessRuleProtection(true, true);
        sc.PurgeAccessRules(container);
        c.SetAccessControl(sc);

        var d = new FileInfo(Write("d.txt"));
        var sd = d.GetAccessControl();
        sd.SetAccessRuleProtection(true, true);
        d.SetAccessControl(sd);
        sd = d.GetAccessControl();
        sd.PurgeAccessRules(container);
        d.SetAccessControl(sd);

        var e = new FileInfo(Write("e.txt"));
        var se = new FileSecurity();
        se.SetAccessRuleProtection(true, false);
        se.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        e.SetAccessControl(se);

        string Acl(FileInfo file) => $"{file.Name}: " + string.Join(", ", file.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(rule => $"{rule.IdentityReference} {rule.AccessControlType} {rule.FileSystemRights} {(rule.IsInherited ? "inh" : "exp")}"));

        var (output, _) = await real.RunAsync("type a.txt & type b.txt & type c.txt & type d.txt & type e.txt & whoami /priv & whoami /groups");

        Assert.Fail($"OUTPUT:\n{output}\nACL:\n{Acl(a)}\n{Acl(b)}\n{Acl(c)}\n{Acl(d)}\n{Acl(e)}\nCONTAINER {container}");
    }
}
