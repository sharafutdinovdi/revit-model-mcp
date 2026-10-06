using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Installer;

static class Replacement
{
    /// <summary>
    ///     Removes shipped files before installation to bypass version rules that preserve developer DLLs and stale manifests.
    /// </summary>
    /// <param name="document">The generated WiX document.</param>
    public static void AddInstallTimeFileRemoval(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var xmlNamespace = document.Root!.Name.Namespace;
        var components = document.Descendants(xmlNamespace + "Component")
            .Where(component => component.Ancestors(xmlNamespace + "Directory").Any());

        foreach (var component in components)
        {
            foreach (var file in component.Elements(xmlNamespace + "File"))
            {
                var fileId = file.Attribute("Id")!.Value;
                var fileName = (string?)file.Attribute("Name")
                    ?? Path.GetFileName(file.Attribute("Source")!.Value.Replace('\\', '/'));
                var removalId = "Rm" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(fileId)));
                component.Add(new XElement(xmlNamespace + "RemoveFile",
                    new XAttribute("Id", removalId),
                    new XAttribute("Name", fileName),
                    new XAttribute("On", "install")));
            }
        }
    }
}
