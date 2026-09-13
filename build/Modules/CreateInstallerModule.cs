using System.Diagnostics;
using System.Text.RegularExpressions;
using Build.Options;
using Microsoft.Extensions.Options;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.FileSystem;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Modules;
using ModularPipelines.Options;
using Shouldly;
using Sourcy.DotNet;
using File = ModularPipelines.FileSystem.File;

namespace Build.Modules;

/// <summary>
///     Create the .msi installer.
/// </summary>
[DependsOn<ResolveVersioningModule>]
[DependsOn<CompileProjectModule>(Optional = true)]
public sealed class CreateInstallerModule(IOptions<BuildOptions> buildOptions) : Module
{
    protected override async Task ExecuteModuleAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var versioningResult = await context.GetModule<ResolveVersioningModule>();
        var versioning = versioningResult.ValueOrDefault!;

        var wixTarget = new File(System.IO.Path.Combine(SolutionRoot.Directory.Path, "src", "RevitModelMcp.Addin", "RevitModelMcp.Addin.csproj"));
        var wixInstaller = new File(Projects.Installer.FullName);
        var wixToolFolder = await InstallWixAsync(context, cancellationToken);

        await context.DotNet().Build(new DotNetBuildOptions
        {
            ProjectSolution = wixInstaller.Path,
            Configuration = "Release"
        }, cancellationToken: cancellationToken);

        var builderFile = wixInstaller.Folder!
            .GetFolder("bin")
            .FindFile(file => file.NameWithoutExtension == wixInstaller.NameWithoutExtension && file.Extension == ".exe");

        builderFile.ShouldNotBeNull($"No installer builder was found for the project: {wixInstaller.NameWithoutExtension}");

        var outputFolder = SolutionRoot.Directory.GetFolder(buildOptions.Value.OutputDirectory);
        var contentFolder = outputFolder.CreateFolder("installer-content");
        contentFolder.Clean();
        var targetDirectories = new List<string>();
        var buildDirectories = wixTarget.Folder!
            .GetFolder("bin")
            .GetFolders(folder => Regex.IsMatch(folder.Name, @"^Release\.R\d{2}$"));

        foreach (var buildDirectory in buildDirectories)
        {
            var yearFolder = contentFolder.CreateFolder(buildDirectory.Name);
            var assemblyFolder = yearFolder.CreateFolder("RevitModelMcp");
            foreach (var source in buildDirectory.GetFiles(file => file.Exists))
            {
                var relativePath = Path.GetRelativePath(buildDirectory.Path, source.Path);
                if (relativePath.Split(Path.DirectorySeparatorChar)[0] == "publish") continue;
                source.Name.StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase)
                    .ShouldBeFalse($"Revit API binaries must not be distributed: {source.Path}");
                var destination = assemblyFolder.GetFile(relativePath);
                destination.Folder!.Create();
                source.CopyTo(destination.Path);
            }

            assemblyFolder.GetFile("RevitModelMcp.dll").Exists.ShouldBeTrue($"Missing add-in: {buildDirectory.Path}");
            wixTarget.Folder.GetFile("RevitModelMcp.addin").CopyTo(yearFolder.GetFile("RevitModelMcp.addin").Path);
            foreach (var name in new[] { "LICENSE", "THIRD-PARTY-NOTICES.md" })
            {
                SolutionRoot.Directory.GetFile(name).CopyTo(yearFolder.GetFile(name).Path);
            }

            targetDirectories.Add(yearFolder.Path);
        }

        targetDirectories.ShouldNotBeEmpty("No Release.R* builds were found to create an installer");

        await context.Shell.Command.ExecuteCommandLineTool(
            new GenericCommandLineToolOptions(builderFile.Path)
            {
                Arguments = [versioning.Version, .. targetDirectories]
            },
            new CommandExecutionOptions
            {
                WorkingDirectory = SolutionRoot.Directory,
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    {"PATH", $"{Environment.GetEnvironmentVariable("PATH")};{wixToolFolder}"}
                }
            }, cancellationToken: cancellationToken);

        var outputFiles = outputFolder.GetFiles(file => file.Extension == ".msi").ToArray();
        outputFiles.Length.ShouldBe(2, "Exactly two installers must be produced");

        foreach (var outputFile in outputFiles)
        {
            context.Summary.KeyValue("Artifacts", "Installer", outputFile.Path);
        }
    }

    /// <summary>
    ///     Installs the WiX toolset required for building installers.
    /// </summary>
    private static async Task<Folder> InstallWixAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var wixToolFolder = Folder.CreateTemporaryFolder();
        await context.DotNet().Tool.Execute(new DotNetToolOptions
        {
            Arguments = ["install", "wix", "--version", "7.*", "--tool-path", wixToolFolder.Path]
        }, cancellationToken: cancellationToken);

        var wixExe = wixToolFolder.GetFile("wix.exe");
        var wixVersion = FileVersionInfo.GetVersionInfo(wixExe.Path).FileVersion!;

        await context.Shell.Command.ExecuteCommandLineTool(
            new GenericCommandLineToolOptions(wixExe.Path)
            {
                Arguments = ["eula", "accept", "wix7"]
            }, cancellationToken: cancellationToken);

        await context.Shell.Command.ExecuteCommandLineTool(
            new GenericCommandLineToolOptions(wixExe.Path)
            {
                Arguments = ["extension", "add", "-g", $"WixToolset.UI.wixext/{wixVersion}"]
            }, cancellationToken: cancellationToken);

        return wixToolFolder;
    }
}
