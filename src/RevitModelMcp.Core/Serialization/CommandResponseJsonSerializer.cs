using System.Diagnostics;
using System.Runtime.Serialization.Json;
using System.Text;
using RevitModelMcp.Core.Models;

namespace RevitModelMcp.Core.Serialization;

public static class CommandResponseJsonSerializer
{
    public static string Serialize<T>(CommandResponse<T> response)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        var serializer = new DataContractJsonSerializer(
            typeof(CommandResponse<T>),
            new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            });
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, response);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

public static class CommandResponseJsonFile
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void Execute(string path, string command, Action<Stopwatch> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            operation(stopwatch);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            Write(
                path,
                CommandResponse<object>.Fail(
                    command,
                    $"Не удалось выполнить команду: {exception}",
                    stopwatch.ElapsedMilliseconds));
        }
    }

    public static void Write<T>(string path, CommandResponse<T> response)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException("Для файла ответа не указан каталог.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, CommandResponseJsonSerializer.Serialize(response), Utf8WithoutBom);
    }
}
