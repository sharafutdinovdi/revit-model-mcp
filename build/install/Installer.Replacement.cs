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

    /// <summary>
    ///     Removes the bundled server folder (and its parent when empty) on uninstall. The folder is created
    ///     by the installer without a component of its own, so Windows Installer otherwise leaves it behind.
    /// </summary>
    /// <param name="document">The generated WiX document.</param>
    /// <param name="productName">The product folder name that contains the server folder.</param>
    public static void AddServerFolderRemoval(XDocument document, string productName)
    {
        ArgumentNullException.ThrowIfNull(document);
        var xmlNamespace = document.Root!.Name.Namespace;
        var serverFolders = document.Descendants(xmlNamespace + "Directory")
            .Where(directory => (string?)directory.Attribute("Name") == "server"
                && (string?)directory.Parent?.Attribute("Name") == productName)
            .ToList();

        foreach (var server in serverFolders)
        {
            var component = server.Elements(xmlNamespace + "Component").FirstOrDefault();
            if (component is null) continue;
            // Children go first: Windows Installer removes folders in the order of the RemoveFolder rows.
            foreach (var folder in new[] { server, server.Parent! })
            {
                var folderId = (string?)folder.Attribute("Id");
                if (folderId is null) continue;
                component.Add(new XElement(xmlNamespace + "RemoveFolder",
                    new XAttribute("Id", "RmDir" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(folderId)))),
                    new XAttribute("Directory", folderId),
                    new XAttribute("On", "uninstall")));
            }
        }
    }
}
