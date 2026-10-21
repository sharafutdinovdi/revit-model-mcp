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
    var hasSymbols = args[0] == "rewrite" && File.Exists(symbolPath);
    string[] referenceNames = ["RevitAPI", "RevitAPIUI"];

    var temporaryPath = assemblyPath + ".revit-api-pin.tmp";
    var temporarySymbolPath = Path.ChangeExtension(temporaryPath, ".pdb");
    try
    {
        using (var assemblyStream = new MemoryStream(File.ReadAllBytes(assemblyPath)))
        using (var symbolStream = hasSymbols ? new MemoryStream(File.ReadAllBytes(symbolPath)) : null)
        using (var assembly = ReadAssembly(assemblyStream, symbolStream))
        {
            hasSymbols = assembly.MainModule.HasSymbols;
            var references = assembly.MainModule.AssemblyReferences;
            foreach (var name in referenceNames)
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
                assembly.Write(temporaryPath, new WriterParameters { WriteSymbols = hasSymbols });
            }
        }

        if (args[0] == "rewrite")
        {
            File.Move(temporaryPath, assemblyPath, true);
            if (hasSymbols)
            {
                File.Move(temporarySymbolPath, symbolPath, true);
            }
        }
    }
    finally
    {
        if (args[0] == "rewrite")
        {
            File.Delete(temporaryPath);
            File.Delete(temporarySymbolPath);
        }
    }

    if (args[0] == "rewrite")
    {
        using (var rewrittenAssembly = AssemblyDefinition.ReadAssembly(assemblyPath))
        {
            foreach (var name in referenceNames)
            {
                var matches = rewrittenAssembly.MainModule.AssemblyReferences.Where(reference => reference.Name == name).ToArray();
                if (matches.Length != 1 || matches[0].Version != expectedVersion)
                {
                    throw new InvalidDataException($"{assemblyPath}: {name} AssemblyRef verification failed. Expected one reference at {expectedVersion}.");
                }
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

static AssemblyDefinition ReadAssembly(Stream assemblyStream, Stream? symbolStream)
{
    try
    {
        return AssemblyDefinition.ReadAssembly(assemblyStream, new ReaderParameters
        {
            InMemory = true,
            ReadSymbols = symbolStream is not null,
            SymbolStream = symbolStream,
            ThrowIfSymbolsAreNotMatching = false
        });
    }
    catch (Exception exception) when (symbolStream is not null &&
                                      exception is ArgumentException or BadImageFormatException)
    {
        assemblyStream.Position = 0;
        return AssemblyDefinition.ReadAssembly(assemblyStream, new ReaderParameters { InMemory = true });
    }
}
