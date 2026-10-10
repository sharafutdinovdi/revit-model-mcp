using System.Text;

namespace RevitModelMcp.Core.Control;

/// <summary>Decodes request bodies as UTF-8 without silently replacing malformed bytes.</summary>
public static class StrictUtf8
{
    private static readonly UTF8Encoding Strict = new(false, true);

    /// <summary>False when the bytes are not well-formed UTF-8.</summary>
    public static bool TryDecode(byte[] bytes, out string text)
    {
        try
        {
            text = Strict.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }
}
