using System.Globalization;
using System.Text;

namespace RevitModelMcp.Core.Connect;

/// <summary>Inspects, adds and removes the server entry in AI app configuration files.</summary>
public sealed class McpClientConfigurator(Func<DateTime>? clock = null)
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.Now);

    /// <summary>Reads the current state of one app.</summary>
    public McpClientStatus Inspect(McpClientTarget target)
    {
        try
        {
            if (!Directory.Exists(target.InstallProbe)) return new McpClientStatus(target, McpClientState.NotInstalled);
            if (!File.Exists(target.ConfigPath)) return new McpClientStatus(target, McpClientState.NotConfigured);
            return InspectText(target, Read(target.ConfigPath).Text);
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return new McpClientStatus(target, McpClientState.Unreadable, exception.Message);
        }
    }

    /// <summary>Adds the entry. Returns <c>Changed = false</c> when it is already there.</summary>
    /// <exception cref="McpConfigException">The app is missing, the file is unreadable or the write failed.</exception>
    public McpConfigChange Add(McpClientTarget target) => Apply(target, add: true);

    /// <summary>Removes the entry. Returns <c>Changed = false</c> when it is not there.</summary>
    /// <exception cref="McpConfigException">The app is missing, the file is unreadable or the write failed.</exception>
    public McpConfigChange Remove(McpClientTarget target) => Apply(target, add: false);

    private McpConfigChange Apply(McpClientTarget target, bool add)
    {
        try
        {
            if (!Directory.Exists(target.InstallProbe)) throw new McpConfigException($"{target.DisplayName} is not installed.");
            var exists = File.Exists(target.ConfigPath);
            if (!exists && !add) return new McpConfigChange(false, null);
            var (text, hadBom) = exists ? Read(target.ConfigPath) : (string.Empty, false);
            var updated = Edit(target, text, add);
            if (updated is null) return new McpConfigChange(false, null);
            var expected = add ? McpClientState.Configured : McpClientState.NotConfigured;
            if (InspectText(target, updated).State != expected)
                throw new McpConfigException("The edit did not produce a valid configuration, so nothing was written.");
            var backup = exists ? Backup(target.ConfigPath) : null;
            WriteAtomically(target.ConfigPath, updated, hadBom, exists);
            return new McpConfigChange(true, backup);
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            throw new McpConfigException(exception.Message);
        }
    }

    private static McpClientStatus InspectText(McpClientTarget target, string text) =>
        target.Format == McpConfigFormat.Toml ? TomlConfigEditor.Inspect(target, text) : JsonConfigEditor.Inspect(target, text);

    private static string? Edit(McpClientTarget target, string text, bool add) => target.Format switch
    {
        McpConfigFormat.Toml => add ? TomlConfigEditor.Add(text) : TomlConfigEditor.Remove(text),
        _ => add ? JsonConfigEditor.Add(target, text) : JsonConfigEditor.Remove(target, text)
    };

    private static bool IsFileError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static (string Text, bool HasBom) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return (Utf8WithoutBom.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0)), hasBom);
    }

    private string Backup(string path)
    {
        var stamp = _clock().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = $"{path}.{stamp}.bak";
        for (var counter = 2; File.Exists(backup); counter++) backup = $"{path}.{stamp}-{counter}.bak";
        File.Copy(path, backup);
        return backup;
    }

    private static void WriteAtomically(string path, string text, bool bom, bool exists)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var body = Utf8WithoutBom.GetBytes(text);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (bom) stream.Write([0xEF, 0xBB, 0xBF], 0, 3);
                stream.Write(body, 0, body.Length);
                stream.Flush(true);
            }
            if (exists) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
