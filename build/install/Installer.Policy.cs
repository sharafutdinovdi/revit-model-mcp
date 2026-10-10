using System.Xml.Linq;
using WixSharp;

namespace Installer;

/// <summary>
///     Machine policy values written by the per-machine installer. They land under
///     HKLM\Software\Policies\RevitModelMcp, which the add-in reads before any user setting.
/// </summary>
static class MachinePolicyValues
{
    const string PolicyKey = @"Software\Policies\RevitModelMcp";

    public static IEnumerable<Property> Properties() =>
    [
        new Property("READONLY", "0") { Secure = true },
        new Property("EXECUTECODE", "") { Secure = true }
    ];

    public static IEnumerable<RegValue> RegValues() =>
    [
        new RegValue(RegistryHive.LocalMachine, PolicyKey, "ReadOnly", "[READONLY]")
        {
            ComponentCondition = "READONLY=\"1\""
        },
        new RegValue(RegistryHive.LocalMachine, PolicyKey, "ExecuteCode", "[EXECUTECODE]")
        {
            ComponentCondition = "EXECUTECODE~=\"off\" OR EXECUTECODE~=\"confirm\""
        }
    ];

    /// <summary>Stores ReadOnly as a DWORD, the type the policy contract defines.</summary>
    public static void MarkReadOnlyAsDword(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var xmlNamespace = document.Root!.Name.Namespace;
        foreach (var value in document.Descendants(xmlNamespace + "RegistryValue")
                     .Where(value => (string?)value.Attribute("Name") == "ReadOnly"
                                     && ((string?)value.Attribute("Key") ?? (string?)value.Parent?.Attribute("Key")) == PolicyKey))
            value.SetAttributeValue("Type", "integer");
    }
}
