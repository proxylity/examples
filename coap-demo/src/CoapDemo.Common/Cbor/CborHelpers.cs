using System.Formats.Cbor;

namespace CoapDemo.Common.Cbor;

/// <summary>Small, hand-rolled CBOR helpers built on the BCL <see cref="System.Formats.Cbor"/> reader/writer.</summary>
public static class CborHelpers
{
    public static byte[] EncodeSingleFieldMap(string key, string value)
    {
        var writer = new CborWriter();
        writer.WriteStartMap(1);
        writer.WriteTextString(key);
        writer.WriteTextString(value);
        writer.WriteEndMap();
        return writer.Encode();
    }

    public static byte[] EncodeMap(IReadOnlyList<(string Key, object Value)> fields)
    {
        var writer = new CborWriter();
        writer.WriteStartMap(fields.Count);
        foreach (var (key, value) in fields)
        {
            writer.WriteTextString(key);
            switch (value)
            {
                case string s: writer.WriteTextString(s); break;
                case bool b: writer.WriteBoolean(b); break;
                case int i: writer.WriteInt64(i); break;
                case long l: writer.WriteInt64(l); break;
                case double d: writer.WriteDouble(d); break;
                case null: writer.WriteNull(); break;
                default: writer.WriteTextString(value.ToString() ?? ""); break;
            }
        }
        writer.WriteEndMap();
        return writer.Encode();
    }

    /// <summary>Reads a flat CBOR map of text-string values into a dictionary. Non-text values are stringified.</summary>
    public static Dictionary<string, string> ReadFlatTextMap(byte[] bytes)
    {
        var reader = new CborReader(bytes);
        var result = new Dictionary<string, string>();
        var count = reader.ReadStartMap() ?? 0;

        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadTextString();
            var value = reader.PeekState() switch
            {
                CborReaderState.TextString => reader.ReadTextString(),
                CborReaderState.UnsignedInteger or CborReaderState.NegativeInteger => reader.ReadInt64().ToString(),
                CborReaderState.Boolean => reader.ReadBoolean().ToString(),
                CborReaderState.Null => ReadNull(reader),
                _ => SkipUnknown(reader),
            };
            result[key] = value;
        }

        reader.ReadEndMap();
        return result;
    }

    private static string ReadNull(CborReader reader)
    {
        reader.ReadNull();
        return "";
    }

    private static string SkipUnknown(CborReader reader)
    {
        reader.SkipValue();
        return "";
    }
}
