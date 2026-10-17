using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace RevitModelMcp.Core.Control;

public static class ResultEnvelope
{
    public static bool TryReadFlags(string json, out bool success, out bool partial)
    {
        success = false;
        partial = false;
        if (!json.TrimEnd().EndsWith("}", StringComparison.Ordinal)) return false;
        try
        {
            using var reader = JsonReaderWriterFactory.CreateJsonReader(
                Encoding.UTF8.GetBytes(json), XmlDictionaryReaderQuotas.Max);
            if (reader.MoveToContent() != XmlNodeType.Element || reader.GetAttribute("type") != "object")
                return false;
            reader.ReadStartElement();
            var foundSuccess = false;
            var foundPartial = false;
            while (reader.MoveToContent() == XmlNodeType.Element)
            {
                if (reader.LocalName == "success" && !foundSuccess)
                {
                    success = reader.ReadElementContentAsString() == "true";
                    foundSuccess = true;
                }
                else if (reader.LocalName == "partial" && !foundPartial)
                {
                    partial = reader.ReadElementContentAsString() == "true";
                    foundPartial = true;
                }
                else
                {
                    reader.Skip();
                }
            }

            reader.ReadEndElement();
            return reader.MoveToContent() == XmlNodeType.None;
        }
        catch (Exception exception) when (exception is XmlException or SerializationException)
        {
            success = false;
            partial = false;
            return false;
        }
    }
}
