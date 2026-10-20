using System;
using System.ComponentModel;
using System.Text;
using MongoDB.Bson.IO;

namespace VelQuery.MongoDB.Serialization;

/// <summary>
/// Immutable, thread-safe element-name decoder for generated serializers. Known field names are matched against the
/// raw UTF-8 bytes in the BSON stream and returned as the shared constant string, so reading a mapped field allocates
/// no name string; unknown names are decoded normally (and then skipped by the serializer).
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class MongoFieldNames : INameDecoder
{
    private readonly string[] _names;
    private readonly byte[][] _utf8;

    /// <summary>Creates a decoder for the given field names (case-sensitive, as in MongoDB).</summary>
    public MongoFieldNames(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _names = (string[])names.Clone();
        _utf8 = new byte[_names.Length][];
        for (var i = 0; i < _names.Length; i++)
        {
            _utf8[i] = Encoding.UTF8.GetBytes(_names[i]);
        }
    }

    /// <inheritdoc />
    public string Decode(BsonStream stream, UTF8Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = stream.ReadCStringBytes();
        var span = bytes.AsSpan();
        var utf8 = _utf8;
        for (var i = 0; i < utf8.Length; i++)
        {
            if (span.SequenceEqual(utf8[i]))
            {
                return _names[i];
            }
        }

        return Utf8Helper.DecodeUtf8String(bytes.Array!, bytes.Offset, bytes.Count, encoding);
    }

    /// <inheritdoc />
    public void Inform(string name)
    {
        // Stateless by design: one instance is shared by every thread reading this document type.
    }
}
