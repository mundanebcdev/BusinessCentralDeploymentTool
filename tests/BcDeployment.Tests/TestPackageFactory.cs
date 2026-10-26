using System.IO.Compression;
using System.Xml.Linq;

namespace BcDeployment.Tests;

internal static class TestPackageFactory
{
    private const uint NavxMagic = 0x5856414E;
    private const int NavxHeaderSize = 40;

    public static void CreateApp(
        string path,
        Guid appId,
        string name,
        Version version,
        params (Guid AppId, string Name, Version MinimumVersion)[] dependencies)
    {
        using var content = new MemoryStream();
        using (var archive = new ZipArchive(content, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry("NavxManifest.xml", CompressionLevel.NoCompression);
            using var manifestStream = manifestEntry.Open();
            var manifest = new XDocument(
                new XElement("Package",
                    new XElement("App",
                        new XAttribute("Id", appId),
                        new XAttribute("Name", name),
                        new XAttribute("Publisher", "Test Publisher"),
                        new XAttribute("Version", version),
                        new XElement("Dependencies",
                            dependencies.Select(dependency =>
                                new XElement("Dependency",
                                    new XAttribute("Id", dependency.AppId),
                                    new XAttribute("Name", dependency.Name),
                                    new XAttribute("Publisher", "Test Publisher"),
                                    new XAttribute("MinVersion", dependency.MinimumVersion)))))));
            manifest.Save(manifestStream);
        }

        content.Position = 0;
        using var output = File.Create(path);
        using var writer = new BinaryWriter(output);
        writer.Write(NavxMagic);
        writer.Write(NavxHeaderSize);
        writer.Write(2U);
        writer.Write(Guid.NewGuid().ToByteArray());
        writer.Write(content.Length);
        writer.Write(NavxMagic);
        content.CopyTo(output);
    }

    public static void CreateZip(string path, params (string EntryName, string AppPath)[] apps)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var app in apps)
        {
            archive.CreateEntryFromFile(app.AppPath, app.EntryName, CompressionLevel.NoCompression);
        }
    }
}
