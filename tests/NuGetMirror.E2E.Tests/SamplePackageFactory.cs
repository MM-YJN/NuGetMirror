using NuGet.Packaging;
using NuGet.Versioning;

namespace NuGetMirror.E2E.Tests;

public static class SamplePackageFactory
{
    public const string Id = "testmirror.sample";
    public const string Version = "1.0.0";
    public const string Version2 = "2.0.0";

    public static byte[] Create() => Create(Version);

    public static byte[] Create(string version)
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "");

            var builder = new PackageBuilder
            {
                Id = Id,
                Version = NuGetVersion.Parse(version),
                Description = "E2E test package",
                Authors = { "TestMirror" },
            };

            builder.Files.Add(new PhysicalPackageFile
            {
                SourcePath = tempFile,
                TargetPath = "lib/net10.0/_._",
            });

            using var stream = new MemoryStream();
            builder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch { }
        }
    }
}
