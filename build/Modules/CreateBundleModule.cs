using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.PackageBuilder;
using Build.Options;
using Microsoft.Extensions.Options;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.FileSystem;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Modules;
using Shouldly;
using Sourcy.DotNet;
using File = ModularPipelines.FileSystem.File;

namespace Build.Modules;

/// <summary>
///     Create the Autodesk .bundle package.
/// </summary>
[DependsOn<ResolveVersioningModule>]
[DependsOn<CompileProjectModule>(Optional = true)]
public sealed partial class CreateBundleModule(IOptions<BuildOptions> buildOptions, IOptions<BundleOptions> bundleOptions) : Module
{
    private const string BundleName = "RevitModelMcp";
    private const string ProductName = "Revit Model MCP";
    private const string ProductDescription = "Read live Revit models from AI clients through MCP. Actions can be turned off with read-only mode.";

    protected override async Task ExecuteModuleAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var versioningResult = await context.GetModule<ResolveVersioningModule>();
        var versioning = versioningResult.ValueOrDefault!;

        var bundleTarget = new File(System.IO.Path.Combine(SolutionRoot.Directory.Path, "src", "RevitModelMcp.Addin", "RevitModelMcp.Addin.csproj"));
        var targetDirectories = bundleTarget.Folder!
            .GetFolder("bin")
            .GetFolders(folder => folder.Name == "publish")
            .OrderBy(folder => folder.Path, StringComparer.Ordinal)
            .ToArray();

        targetDirectories.ShouldNotBeEmpty("No content were found to create a bundle");

        var outputFolder = SolutionRoot.Directory.GetFolder(buildOptions.Value.OutputDirectory);
        var bundleFolder = outputFolder.CreateFolder($"{BundleName}.bundle");
        var contentFolder = bundleFolder.CreateFolder("Contents");
        var manifestFile = bundleFolder.GetFile("PackageContents.xml");

        PackFiles(targetDirectories, contentFolder);
        GenerateManifest(targetDirectories, manifestFile, versioning);

        var outputFile = outputFolder.GetFile($"{bundleFolder.Name}.zip");
        if (outputFile.Exists)
        {
            outputFile.Delete();
        }

        ZipFile.CreateFromDirectory(bundleFolder.Path, outputFile.Path, CompressionLevel.Optimal, includeBaseDirectory: true);
        await bundleFolder.DeleteAsync(cancellationToken);

        context.Summary.KeyValue("Artifacts", "Bundle", outputFile.Path);
    }

    private static void PackFiles(Folder[] targetDirectories, Folder contentFolder)
    {
        foreach (var targetDirectory in targetDirectories)
        {
            TryParseVersion(targetDirectory.Path, out var version)
                .ShouldBeTrue($"Could not parse version from directory name: {targetDirectory.Path}");

            var versionFolder = contentFolder.CreateFolder(version);
            foreach (var filePath in targetDirectory.GetFiles(file => file.Exists))
            {
                var relativePath = Path.GetRelativePath(targetDirectory.Path, filePath.Path);
                var destinationPath = versionFolder.GetFile(relativePath);
                if (!destinationPath.Folder!.Exists)
                {
                    destinationPath.Folder!.Create();
                }

                Regex.IsMatch(filePath.Name, @"^(AdWindows|UIFramework|RevitAPI|RevitNET).*\.dll$", RegexOptions.IgnoreCase)
                    .ShouldBeFalse($"Revit API assemblies must not be packed: {filePath.Path}");

                filePath.CopyTo(destinationPath.Path);
            }
        }
    }

    /// <summary>
    ///     Generate the Autodesk manifest.
    /// </summary>
    private void GenerateManifest(Folder[] targetDirectories, File manifestDirectory, ResolveVersioningResult versioning)
    {
        var upgradeCode = bundleOptions.Value.UpgradeCode!;
        var productCode = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{upgradeCode}:{versioning.Version}")));

        BuilderUtils.Build<PackageContentsBuilder>(builder =>
        {
            builder.ApplicationPackage.Create()
                .ProductType(ProductTypes.Application)
                .AutodeskProduct(AutodeskProducts.Revit)
                .Name(ProductName)
                .Description(ProductDescription)
                .AppVersion(versioning.VersionPrefix)
                .FriendlyVersion(versioning.Version)
                .ProductCode(productCode.ToString("B").ToUpperInvariant())
                .UpgradeCode(upgradeCode.ToUpperInvariant())
                .Author(bundleOptions.Value.VendorName)
                .OnlineDocumentation(bundleOptions.Value.VendorUrl);

            builder.CompanyDetails.Create(bundleOptions.Value.VendorName)
                .Email(bundleOptions.Value.VendorEmail)
                .Url(bundleOptions.Value.VendorUrl);

            foreach (var targetDirectory in targetDirectories)
            {
                TryParseVersion(targetDirectory.Path, out var version)
                    .ShouldBeTrue($"Could not parse version from directory name: {targetDirectory.Path}");

                var addinManifests = targetDirectory.GetFiles(file => file.Extension == ".addin");
                foreach (var addinManifest in addinManifests)
                {
                    var relativePath = Path.GetRelativePath(targetDirectory.Path, addinManifest.Path);

                    builder.Components.CreateEntry($"Revit {version}")
                        .RevitPlatform(int.Parse(version))
                        .AppName(ProductName)
                        .ModuleName($"./Contents/{version}/{relativePath.Replace('\\', '/')}");
                }
            }
        }, manifestDirectory);
    }

    /// <summary>
    ///     Parse a version string from the given input.
    /// </summary>
    private static bool TryParseVersion(string input, [NotNullWhen(true)] out string? version)
    {
        version = null;
        var match = VersionRegex().Match(input);
        if (!match.Success) return false;

        switch (match.Value.Length)
        {
            case 4:
                version = match.Value;
                return true;
            case 2:
                version = $"20{match.Value}";
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    ///     A regular expression to match the last sequence of numeric characters in a string.
    /// </summary>
    [GeneratedRegex(@"(\d+)(?!.*\d)")]
    private static partial Regex VersionRegex();
}
