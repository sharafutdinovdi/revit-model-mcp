using System.Runtime.Serialization.Json;
using System.Text;
using RevitModelMcp.Core.Batch;

namespace RevitModelMcp.BatchSupervisor;

internal sealed class BatchRunStore(string directory)
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string _directory = directory;

    public string DirectoryPath => _directory;
    public bool CancellationRequested => File.Exists(Path.Combine(_directory, "cancel.json"));

    public FileStream AcquireLease() => new(Path.Combine(_directory, "supervisor.lock"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None);

    public BatchRun Read() => ReadJson<BatchRun>(Path.Combine(_directory, "run.json"));
    public BatchLaunchRequest ReadLaunch() => ReadJson<BatchLaunchRequest>(Path.Combine(_directory, "launch.json"));

    public void Write(BatchRun run)
    {
        var path = Path.Combine(_directory, "run.json");
        var temporary = Path.Combine(_directory, $"run.{Guid.NewGuid():N}.tmp");
        var serializer = Serializer<BatchRun>();
        try
        {
            using (var stream = File.Create(temporary)) serializer.WriteObject(stream, run);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void WriteSnapshot(string fileName, string json)
    {
        if (Path.GetFileName(fileName) != fileName) throw new ArgumentException("Invalid snapshot name.");
        var path = Path.Combine(_directory, fileName);
        var temporary = Path.Combine(_directory, $"snapshot.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8.GetBytes(json);
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static T ReadJson<T>(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.ReadByte() != 0xEF || stream.ReadByte() != 0xBB || stream.ReadByte() != 0xBF)
            stream.Position = 0;
        return (T)(Serializer<T>().ReadObject(stream) ?? throw new InvalidDataException("Empty batch JSON."));
    }

    private static DataContractJsonSerializer Serializer<T>() => new(typeof(T),
        new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
}
