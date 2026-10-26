using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace BcDeployment;

public static class NavxPackageReader
{
    private const uint NavxMagic = 0x5856414E;
    private const int NavxHeaderSize = 40;

    public static AppPackage Read(string filePath, string displayFileName, long maximumBytes)
    {
        var file = new FileInfo(filePath);
        if (file.Length is <= 0 || file.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"App '{displayFileName}' must be between 1 byte and {maximumBytes / (1024 * 1024)} MB.");
        }

        using var stream = file.OpenRead();
        using var reader = new BinaryReader(stream);
        if (stream.Length < NavxHeaderSize
            || reader.ReadUInt32() != NavxMagic
            || reader.ReadUInt32() != NavxHeaderSize
            || reader.ReadUInt32() > 2)
        {
            throw new InvalidDataException($"App '{displayFileName}' has an unsupported NAVX package header.");
        }

        _ = new Guid(reader.ReadBytes(16));
        var contentLength = reader.ReadInt64();
        if (reader.ReadUInt32() != NavxMagic
            || contentLength <= 0
            || contentLength > maximumBytes
            || stream.Position + contentLength > stream.Length)
        {
            throw new InvalidDataException($"App '{displayFileName}' has invalid NAVX package metadata.");
        }

        var content = reader.ReadBytes(checked((int)contentLength));
        using var packageContent = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(packageContent, ZipArchiveMode.Read);
        var manifestEntry = archive.Entries.SingleOrDefault(entry =>
            entry.FullName.Equals("NavxManifest.xml", StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is null || manifestEntry.Length > 1024 * 1024)
        {
            throw new InvalidDataException($"App '{displayFileName}' has no valid NavxManifest.xml.");
        }

        using var manifestStream = manifestEntry.Open();
        using var xmlReader = XmlReader.Create(manifestStream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024
        });
        var document = XDocument.Load(xmlReader, LoadOptions.None);
        var app = document.Descendants().SingleOrDefault(element => element.Name.LocalName == "App")
            ?? throw new InvalidDataException($"App '{displayFileName}' has no App manifest element.");

        var appId = ParseGuidAttribute(app, "Id", displayFileName);
        var name = RequiredAttribute(app, "Name", displayFileName);
        var publisher = RequiredAttribute(app, "Publisher", displayFileName);
        var version = ParseVersion(RequiredAttribute(app, "Version", displayFileName), displayFileName);
        var dependencies = document.Descendants()
            .Where(element => element.Name.LocalName == "Dependencies")
            .Elements()
            .Select(element => new AppDependency(
                ParseGuidAttribute(element, "Id", displayFileName),
                Attribute(element, "Name") ?? string.Empty,
                Attribute(element, "Publisher") ?? string.Empty,
                ParseVersion(Attribute(element, "MinVersion") ?? "0.0.0.0", displayFileName)))
            .ToArray();

        return new AppPackage(filePath, displayFileName, appId, name, publisher, version, dependencies);
    }

    private static Guid ParseGuidAttribute(XElement element, string name, string fileName) =>
        Guid.TryParse(Attribute(element, name), out var value)
            ? value
            : throw new InvalidDataException($"App '{fileName}' has an invalid {name} value.");

    private static string RequiredAttribute(XElement element, string name, string fileName) =>
        Attribute(element, name) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"App '{fileName}' has no {name} value.");

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static Version ParseVersion(string value, string fileName) =>
        Version.TryParse(value, out var version)
            ? version
            : throw new InvalidDataException($"App '{fileName}' has an invalid version '{value}'.");
}
