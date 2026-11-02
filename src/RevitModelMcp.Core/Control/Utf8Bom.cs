namespace RevitModelMcp.Core.Control;

/// <summary>Provides removal of a leading UTF-8 byte order mark.</summary>
public static class Utf8Bom
{
    /// <summary>Returns the bytes without a leading UTF-8 byte order mark.</summary>
    /// <param name="bytes">The UTF-8 content.</param>
    /// <returns>The content without the mark, or the original array when no mark is present.</returns>
    public static byte[] Strip(byte[] bytes)
    {
        if (bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF) return bytes;
        var content = new byte[bytes.Length - 3];
        Array.Copy(bytes, 3, content, 0, content.Length);
        return content;
    }
}
