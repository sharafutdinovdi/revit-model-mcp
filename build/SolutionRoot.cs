using ModularPipelines.FileSystem;
using Sourcy.DotNet;

namespace Build;

public static class SolutionRoot
{
    private static readonly string RootPath = Resolve();

    public static Folder Directory { get; } = ToFolder(RootPath);

    private static Folder ToFolder(string path)
    {
        Folder? folder = path;
        return folder!;
    }

    private static string Resolve()
    {
        var directory = Projects.Build.Directory;
        while (directory is not null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "RevitModelMcp.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "RevitModelMcp.sln was not found above the build project.");
    }
}
