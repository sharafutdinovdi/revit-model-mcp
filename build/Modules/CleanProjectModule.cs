using Build.Options;
using Microsoft.Extensions.Options;
using ModularPipelines.Attributes;
using ModularPipelines.Conditions;
using ModularPipelines.Context;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Modules;
using Sourcy.DotNet;

namespace Build.Modules;

/// <summary>
///     Clean projects and artifact directories.
/// </summary>
[SkipIf<IsCI>]
public sealed class CleanProjectModule(IOptions<BuildOptions> buildOptions) : SyncModule
{
    protected override void ExecuteModule(IModuleContext context, CancellationToken cancellationToken)
    {
        // Cleanup is scoped to the solution directory; the git root may contain
        // neighboring add-ins whose bin/obj directories must be preserved. See SolutionRoot.
        var rootDirectory = SolutionRoot.Directory;
        var outputDirectory = rootDirectory.GetFolder(buildOptions.Value.OutputDirectory);
        var buildOutputDirectories = rootDirectory
            .GetFolders(folder => folder.Name is "bin" or "obj")
            .Where(folder => folder.Parent != Projects.Build.Directory);

        foreach (var buildFolder in buildOutputDirectories)
        {
            buildFolder.Clean();
        }

        if (outputDirectory.Exists)
        {
            outputDirectory.Clean();
        }
    }
}