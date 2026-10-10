using RevitModelMcp.Core.Policy;

namespace RevitModelMcp.Core.Tests.Policy;

public sealed class PolicyFileTrustTests
{
    private const string System = "S-1-5-18";
    private const string Admins = "S-1-5-32-544";
    private const string Users = "S-1-5-32-545";
    private const string Standard = "S-1-5-21-1-2-3-1001";

    [Test]
    public async Task AdministratorOwnedAndUsersReadOnly_IsTrusted()
    {
        AccessEntry[] entries = [new(System, true), new(Admins, true), new(Users, false)];
        await Assert.That(PolicyFileTrust.Check("the policy file", Admins, entries, false)).IsNull();
    }

    [Test]
    public async Task OwnedByStandardUser_IsRefused()
    {
        AccessEntry[] entries = [new(Admins, true)];
        await Assert.That(PolicyFileTrust.Check("the policy folder", Standard, entries, false)).Contains("not owned by an administrator");
        await Assert.That(PolicyFileTrust.Check("the policy folder", null, entries, false)).Contains("not owned by an administrator");
    }

    [Test]
    public async Task WritableByUsersOrOwnerRights_IsRefused()
    {
        await Assert.That(PolicyFileTrust.Check("the policy file", Admins, [new(Users, true)], false)).Contains("non-administrator");
        await Assert.That(PolicyFileTrust.Check("the policy file", Admins, [new(Standard, true)], false)).Contains(Standard);
        await Assert.That(PolicyFileTrust.Check("the policy file", Admins, [new("S-1-3-0", true)], false)).Contains("non-administrator");
    }

    [Test]
    public async Task ReparsePoint_IsRefused()
    {
        await Assert.That(PolicyFileTrust.Check("the policy file", Admins, [], true)).Contains("link");
    }
}
