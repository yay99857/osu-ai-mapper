using System.IO.Compression;
using System.Text;

namespace OsuAi.OsuAdapter.Tests;

public class SafePackageReaderTests
{
    private string workDir = null!;

    [SetUp]
    public void CreateWorkDir()
    {
        workDir = Path.Combine(Path.GetTempPath(), "osu-ai-mapper-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
    }

    [TearDown]
    public void DeleteWorkDir() => Directory.Delete(workDir, recursive: true);

    private string Zip(params (string Name, byte[] Data)[] entries) => Zip(entries.Select(e => (e.Name, e.Data, 0)).ToArray());

    private string Zip((string Name, byte[] Data, int UnixMode)[] entries)
    {
        string path = Path.Combine(workDir, $"{Guid.NewGuid():N}.osz");

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, data, mode) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                if (mode != 0)
                    entry.ExternalAttributes = mode << 16;

                using var stream = entry.Open();
                stream.Write(data);
            }
        }

        return path;
    }

    private static byte[] Osu => File.ReadAllBytes(Path.Combine(Fixtures.OsuDirectory, "simple-120bpm.osu"));

    [Test]
    public void ReadsOsuFilesAndOnlyRequestedEntries()
    {
        string path = Zip(("map.osu", Osu), ("audio.wav", [1, 2, 3]), ("bg/background.jpg", [4]));

        var contents = SafePackageReader.ReadWith(path, ["audio.wav"]);

        Assert.That(contents.Entries.Select(e => e.Path), Is.EquivalentTo(new[] { "map.osu", "audio.wav", "bg/background.jpg" }));
        Assert.That(contents.Read(contents.Resolve("AUDIO.WAV")!), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(contents.Read(contents.OsuFiles.Single()), Is.EqualTo(Osu));
        Assert.Throws<InvalidOperationException>(() => contents.Read(contents.Resolve("bg/background.jpg")!));
        Assert.That(contents.SourceSha256, Has.Length.EqualTo(64));
    }

    [TestCase("../evil.osu")]
    [TestCase("bg/../../evil.wav")]
    [TestCase("/etc/evil.wav")]
    [TestCase("C:/evil.wav")]
    [TestCase("CON.wav")]
    [TestCase("bg/nul")]
    public void RejectsUnsafePaths(string name) =>
        Assert.Throws<UnsafePackageException>(() => SafePackageReader.Read(Zip(("map.osu", Osu), (name, new byte[] { 0 }))));

    [Test]
    public void AcceptsBackslashSeparatorsWithAWarning()
    {
        var contents = SafePackageReader.Read(Zip(("map.osu", Osu), ("sb\\sprite.png", new byte[] { 0 })));

        Assert.That(contents.Entries.Select(e => e.Path), Does.Contain("sb/sprite.png"));
        Assert.That(contents.Warnings, Has.Count.EqualTo(1));
    }

    [Test]
    public void RejectsCaseInsensitiveDuplicates() =>
        Assert.Throws<UnsafePackageException>(() => SafePackageReader.Read(Zip(("map.osu", Osu), ("Audio.wav", new byte[] { 0 }), ("audio.WAV", new byte[] { 1 }))));

    [Test]
    public void RejectsSymlinks()
    {
        const int symlink = 0xA1FF; // S_IFLNK | 0777
        string path = Zip([("map.osu", Osu, 0), ("link.wav", Encoding.UTF8.GetBytes("/etc/passwd"), symlink)]);

        Assert.Throws<UnsafePackageException>(() => SafePackageReader.Read(path));
    }

    [Test]
    public void RejectsZipBombs()
    {
        // 64 MiB of zeros compresses to about 64 KiB: a 1000:1 ratio.
        string path = Zip(("map.osu", Osu), ("bomb.wav", new byte[64 << 20]));

        var e = Assert.Throws<UnsafePackageException>(() => SafePackageReader.Read(path));
        Assert.That(e!.Message, Does.Contain("zip bomb"));
    }

    [Test]
    public void CountsRealBytesAgainstLimits()
    {
        string path = Zip(("map.osu", Osu), ("audio.wav", new byte[4096]));
        var limits = new PackageLimits { MaxEntryBytes = 1024 };

        Assert.Throws<UnsafePackageException>(() => SafePackageReader.Read(path, limits));
        Assert.Throws<UnsafePackageException>(() => SafePackageReader.Read(path, new PackageLimits { MaxOsuBytes = 100 }));
        Assert.Throws<UnsafePackageException>(() => SafePackageReader.Read(path, new PackageLimits { MaxEntries = 1 }));
    }

    [Test]
    public void RejectsFilesThatAreNotZips()
    {
        string path = Path.Combine(workDir, "fake.osz");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("not a zip at all"));

        Assert.Throws<UnsafePackageException>(() => SafePackageReader.Read(path));
    }

    [Test]
    public void ReadsBeatmapFolders()
    {
        string folder = Path.Combine(workDir, "set");
        Directory.CreateDirectory(Path.Combine(folder, "sb"));
        File.WriteAllBytes(Path.Combine(folder, "map.osu"), Osu);
        File.WriteAllBytes(Path.Combine(folder, "audio.wav"), [9]);
        File.WriteAllBytes(Path.Combine(folder, "sb", "a.png"), [7]);

        var first = SafePackageReader.ReadWith(folder, ["audio.wav"]);
        var second = SafePackageReader.Read(folder);

        Assert.That(first.Entries.Select(e => e.Path), Is.EqualTo(new[] { "audio.wav", "map.osu", "sb/a.png" }));
        Assert.That(first.Read(first.Resolve("audio.wav")!), Is.EqualTo(new byte[] { 9 }));
        Assert.That(second.SourceSha256, Is.EqualTo(first.SourceSha256), "folder identity is deterministic");
    }
}
