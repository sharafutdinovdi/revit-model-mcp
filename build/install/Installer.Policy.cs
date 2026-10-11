using System.Xml.Linq;
using WixSharp;

namespace Installer;

/// <summary>
///     Machine policy values written by the per-machine installer. They land under
///     HKLM\Software\Policies\RevitModelMcp, which the add-in reads before any user setting.
///     <para>
///         Ownership rule: the installer writes a value only when its property is set explicitly (READONLY=1,
///         EXECUTECODE=off|confirm) or was set by an earlier installer run. It remembers its own writes in a marker
///         key under HKLM\Software\RevitModelMcp\MsiPolicy, never by reading the policy key, so a value that Group
///         Policy or Intune owns is never rewritten, reset by repair, or removed by upgrade or uninstall.
///         ReadOnly is written as the literal 1, so a repair can never write 0.
///         A value on the command line always wins over the remembered one. Pass READONLY=0 or EXECUTECODE=confirm on an upgrade to take back a value the installer wrote earlier.
///     </para>
/// </summary>
static class MachinePolicyValues
{
    const string PolicyKey = @"Software\Policies\RevitModelMcp";
    const string MarkerKey = @"Software\RevitModelMcp\MsiPolicy";

    const string ReadOnlySet = "READONLY=\"1\"";
    const string ExecuteCodeSet = "EXECUTECODE~=\"off\" OR EXECUTECODE~=\"confirm\"";

    public static IEnumerable<Property> Properties() =>
    [
        new Property("READONLY", "") { Secure = true },
        new Property("EXECUTECODE", "") { Secure = true },
        new RegValueProperty("RMM_REMEMBERED_READONLY", RegistryHive.LocalMachine, MarkerKey, "ReadOnly", "") { Secure = true },
        new RegValueProperty("RMM_REMEMBERED_EXECUTECODE", RegistryHive.LocalMachine, MarkerKey, "ExecuteCode", "") { Secure = true }
    ];

    /// <summary>
    ///     The search result goes into private properties, so AppSearch cannot overwrite a value given on the command line.
    ///     Only an empty READONLY or EXECUTECODE takes the remembered value.
    /// </summary>
    public static IEnumerable<WixSharp.Action> RememberActions() =>
    [
        new SetPropertyAction("READONLY", "[RMM_REMEMBERED_READONLY]", Return.check, When.After, Step.AppSearch,
            new Condition("READONLY=\"\" AND RMM_REMEMBERED_READONLY<>\"\"")),
        new SetPropertyAction("EXECUTECODE", "[RMM_REMEMBERED_EXECUTECODE]", Return.check, When.After, Step.AppSearch,
            new Condition("EXECUTECODE=\"\" AND RMM_REMEMBERED_EXECUTECODE<>\"\""))
    ];

    public static IEnumerable<RegValue> RegValues() =>
    [
        new RegValue(RegistryHive.LocalMachine, PolicyKey, "ReadOnly", "1") { ComponentCondition = ReadOnlySet },
        new RegValue(RegistryHive.LocalMachine, MarkerKey, "ReadOnly", "1") { ComponentCondition = ReadOnlySet },
        new RegValue(RegistryHive.LocalMachine, PolicyKey, "ExecuteCode", "[EXECUTECODE]") { ComponentCondition = ExecuteCodeSet },
        new RegValue(RegistryHive.LocalMachine, MarkerKey, "ExecuteCode", "[EXECUTECODE]") { ComponentCondition = ExecuteCodeSet }
    ];

    /// <summary>Stores ReadOnly under the policy key as a DWORD, the type the policy contract defines. The marker stays a string.</summary>
    public static void MarkReadOnlyAsDword(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var xmlNamespace = document.Root!.Name.Namespace;
        foreach (var value in document.Descendants(xmlNamespace + "RegistryValue")
                     .Where(value => (string?)value.Attribute("Name") == "ReadOnly"
                                     && ((string?)value.Attribute("Key") ?? (string?)value.Parent?.Attribute("Key")) == PolicyKey))
            value.SetAttributeValue("Type", "integer");
    }

    /// <summary>
    ///     PowerShell that makes %ProgramData%\RevitModelMcp a folder only administrators can change. It runs as SYSTEM.
    ///     A folder that is a link, or is owned by anyone but Administrators or SYSTEM, is refused: the install fails
    ///     instead of writing through something a standard user planted. A good folder gets its ACL reset to
    ///     Administrators and SYSTEM full control and Users read and execute, with inheritance cut.
    /// </summary>
    public const string EnsureProgramDataFolderScript =
        "$ErrorActionPreference = 'Stop'; " +
        "$directory = Join-Path $env:ProgramData 'RevitModelMcp'; " +
        "$admins = New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-544'; " +
        "$system = New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-18'; " +
        "$users = New-Object System.Security.Principal.SecurityIdentifier 'S-1-5-32-545'; " +
        "$inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'; " +
        "$none = [System.Security.AccessControl.PropagationFlags]::None; " +
        "$allow = [System.Security.AccessControl.AccessControlType]::Allow; " +
        "function Add-Rule($acl, $sid, $rights) { $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid, $rights, $inherit, $none, $allow))) }; " +
        "if (Test-Path -LiteralPath $directory) { " +
        "$item = Get-Item -LiteralPath $directory -Force; " +
        "if (-not $item.PSIsContainer -or ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { throw \"$directory is not a plain folder.\" }; " +
        "$acl = Get-Acl -LiteralPath $directory; " +
        "$owner = $acl.GetOwner([System.Security.Principal.SecurityIdentifier]); " +
        "if ($owner.Value -ne $admins.Value -and $owner.Value -ne $system.Value) { throw \"$directory is owned by $owner, not by Administrators or SYSTEM. Remove it and run the installer again.\" }; " +
        "$acl.SetAccessRuleProtection($true, $false); " +
        "foreach ($rule in @($acl.Access)) { [void]$acl.RemoveAccessRuleAll($rule) }; " +
        "Add-Rule $acl $admins 'FullControl'; Add-Rule $acl $system 'FullControl'; Add-Rule $acl $users 'ReadAndExecute'; " +
        "Set-Acl -LiteralPath $directory -AclObject $acl " +
        "} else { " +
        "$acl = New-Object System.Security.AccessControl.DirectorySecurity; " +
        "$acl.SetOwner($admins); $acl.SetAccessRuleProtection($true, $false); " +
        "Add-Rule $acl $admins 'FullControl'; Add-Rule $acl $system 'FullControl'; Add-Rule $acl $users 'ReadAndExecute'; " +
        "[void][System.IO.Directory]::CreateDirectory($directory, $acl) " +
        "}; ";
}
