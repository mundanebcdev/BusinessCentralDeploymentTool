using System.IO.Compression;

namespace BcDeployment.Tests;

[TestClass]
public sealed class PackageBundleReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"bc-package-tests-{Guid.NewGuid():N}");

    public PackageBundleReaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [TestMethod]
    public void Prepare_ZipWithDependency_ReturnsDependencyFirst()
    {
        var dependencyId = Guid.NewGuid();
        var appId = Guid.NewGuid();
        var dependencyPath = Path.Combine(_root, "Dependency.app");
        var appPath = Path.Combine(_root, "Main.app");
        var zipPath = Path.Combine(_root, "bundle.zip");
        TestPackageFactory.CreateApp(dependencyPath, dependencyId, "Dependency", new Version(1, 0, 0, 0));
        TestPackageFactory.CreateApp(
            appPath,
            appId,
            "Main",
            new Version(2, 0, 0, 0),
            (dependencyId, "Dependency", new Version(1, 0, 0, 0)));
        TestPackageFactory.CreateZip(zipPath, ("Main.app", appPath), ("Dependency.app", dependencyPath));

        using var packageSet = CreateReader().Prepare(zipPath);

        CollectionAssert.AreEqual(
            new[] { dependencyId, appId },
            packageSet.Packages.Select(package => package.AppId).ToArray());
        Assert.IsEmpty(packageSet.ExternalDependencies);
    }

    [TestMethod]
    public void Prepare_ZipWithTraversalEntry_RejectsArchive()
    {
        var zipPath = Path.Combine(_root, "unsafe.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("../outside.app");
        }

        Assert.ThrowsExactly<InvalidDataException>(() => CreateReader().Prepare(zipPath));
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private PackageBundleReader CreateReader() => new(new AdminCenterOptions
    {
        AuthMode = "ClientSecret",
        AllowedAppRoots = [_root]
    });
}
