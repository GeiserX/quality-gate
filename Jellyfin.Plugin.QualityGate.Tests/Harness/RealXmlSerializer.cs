using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Xml;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.QualityGate.Tests.Harness;

/// <summary>
/// An <see cref="IXmlSerializer"/> backed by the real <see cref="System.Xml.Serialization.XmlSerializer"/>,
/// set up the way Jellyfin's own server implementation sets it up: one serializer per type,
/// an indented writer on save, a plain <see cref="XmlReader"/> on load.
///
/// Jellyfin's implementation lives in the server assembly, which is not a NuGet package, so it
/// cannot be referenced here. What matters is that nothing is mocked: every quirk of
/// <c>XmlSerializer</c>, such as appending loaded items to a list its initialiser already
/// filled, happens here exactly as it does on a server.
/// </summary>
public sealed class RealXmlSerializer : IXmlSerializer
{
    private static readonly ConcurrentDictionary<Type, System.Xml.Serialization.XmlSerializer> Serializers = new();

    /// <inheritdoc />
    public object? DeserializeFromStream(Type type, Stream stream)
    {
        using var reader = XmlReader.Create(stream);
        return GetSerializer(type).Deserialize(reader);
    }

    /// <inheritdoc />
    public void SerializeToStream(object obj, Stream stream)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
        using var xmlWriter = XmlWriter.Create(writer, new XmlWriterSettings { Indent = true });
        GetSerializer(obj.GetType()).Serialize(xmlWriter, obj);
    }

    /// <inheritdoc />
    public void SerializeToFile(object obj, string file)
    {
        using var stream = new FileStream(file, FileMode.Create, FileAccess.Write);
        SerializeToStream(obj, stream);
    }

    /// <inheritdoc />
    public object? DeserializeFromFile(Type type, string file)
    {
        using var stream = File.OpenRead(file);
        return DeserializeFromStream(type, stream);
    }

    /// <inheritdoc />
    public object? DeserializeFromBytes(Type type, byte[] buffer)
    {
        using var stream = new MemoryStream(buffer, writable: false);
        return DeserializeFromStream(type, stream);
    }

    private static System.Xml.Serialization.XmlSerializer GetSerializer(Type type)
        => Serializers.GetOrAdd(type, t => new System.Xml.Serialization.XmlSerializer(t));
}
