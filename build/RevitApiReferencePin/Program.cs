using Mono.Cecil;

if (args.Length != 3 || args[0] is not ("rewrite" or "verify") ||
    !Version.TryParse(args[2], out var expectedVersion) || expectedVersion.Build < 0 || expectedVersion.Revision < 0)
{
    Console.Error.WriteLine("Usage: RevitApiReferencePin <rewrite|verify> <assembly> <expected-assembly-version>");
    return 2;
}

try
{
    var assemblyPath = Path.GetFullPath(args[1]);
    var symbolPath = Path.ChangeExtension(assemblyPath, ".pdb");
    var hasSymbols = File.Exists(symbolPath);

    using (var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters { ReadSymbols = hasSymbols }))
    {
        var references = assembly.MainModule.AssemblyReferences;
        foreach (var name in new[] { "RevitAPI", "RevitAPIUI" })
        {
            var matches = references.Where(reference => reference.Name == name).ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidDataException($"{assemblyPath}: expected exactly one {name} AssemblyRef, found {matches.Length}.");
            }

            if (args[0] == "rewrite")
            {
                matches[0].Version = expectedVersion;
            }
            else if (matches[0].Version != expectedVersion)
            {
                throw new InvalidDataException($"{assemblyPath}: {name} AssemblyRef is {matches[0].Version}, expected {expectedVersion}.");
            }
        }

        if (args[0] == "rewrite")
        {
            var temporaryPath = assemblyPath + ".revit-api-pin.tmp";
            var temporarySymbolPath = Path.ChangeExtension(temporaryPath, ".pdb");
            try
            {
                assembly.Write(temporaryPath, new WriterParameters { WriteSymbols = hasSymbols });
                File.Move(temporaryPath, assemblyPath, true);
                if (hasSymbols)
                {
                    File.Move(temporarySymbolPath, symbolPath, true);
                }
            }
            finally
            {
                File.Delete(temporaryPath);
                File.Delete(temporarySymbolPath);
            }
        }
    }

    if (args[0] == "rewrite")
    {
        using var rewrittenAssembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        foreach (var name in new[] { "RevitAPI", "RevitAPIUI" })
        {
            var matches = rewrittenAssembly.MainModule.AssemblyReferences.Where(reference => reference.Name == name).ToArray();
            if (matches.Length != 1 || matches[0].Version != expectedVersion)
            {
                throw new InvalidDataException($"{assemblyPath}: {name} AssemblyRef verification failed. Expected one reference at {expectedVersion}.");
            }
        }
    }

    Console.WriteLine($"PASS: {assemblyPath} has RevitAPI and RevitAPIUI AssemblyRefs at {expectedVersion}.");
    return 0;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException or ArgumentException)
{
    Console.Error.WriteLine($"ERROR: {exception.Message}");
    return 1;
}
