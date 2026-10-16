using System.IO.Compression;
using System.Text;
using OsuAi.DatasetBuilder;

namespace OsuAi.DatasetBuilder.Tests;

public class DatasetBuilderTests
{
    private string work = null!;
    private DatasetStore store = null!;
    private RightsGrant rights = null!;

    // A minimal RIFF/WAVE header is enough for the sniffer; the builder never decodes audio.
    private static readonly byte[] wav = [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVEfmt "u8, 1, 2, 3, 4];

    [SetUp]
    public void SetUp()
    {
        work = Path.Combine(Path.GetTempPath(), "osu-ai-dataset-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(work, "sources"));
        store = new DatasetStore(Path.Combine(work, "dataset"));
        rights = new RightsGrant { Id = "test", Scope = RightsScope.Evaluation, Source = "tests", GrantedBy = "tests", GrantedAt = DateTimeOffset.UtcNow };
        store.AddRights(rights);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(work, recursive: true);

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", name));

    private static string WithMetadata(string osu, string title, string artist) =>
        osu.Replace("Title:Simple 120 BPM", $"Title:{title}").Replace("Artist:osu-ai-mapper fixtures", $"Artist:{artist}");

    private string Package(string name, params (string Entry, byte[] Data)[] entries)
    {
        string path = Path.Combine(work, "sources", name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (entry, data) in entries)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(data);
        }

        return path;
    }

    private IngestSummary Ingest() => new Ingestor(store, rights, TextWriter.Null).Ingest(Path.Combine(work, "sources"));

    [Test]
    public void AcceptsAndStoresContentAddressed()
    {
        Package("a.osz", ("map.osu", Encoding.UTF8.GetBytes(Fixture("simple-120bpm.osu"))), ("audio.wav", wav));

        var summary = Ingest();
        var map = store.Maps.Single();

        Assert.That(summary.Accepted, Is.EqualTo(1));
        Assert.That(map.Status, Is.EqualTo(IngestStatus.Accepted));
        Assert.That(map.AudioContainer, Is.EqualTo("wav"));
        Assert.That(map.AudioRef, Is.EqualTo($"audio/{map.AudioSha256}.wav"));
        Assert.That(File.Exists(store.PathOf(map.IrRef!)));
        Assert.That(map.RoundTripLossless, Is.True);
        Assert.That(map.MainBpm, Is.EqualTo(120));
        Assert.That(map.RightsRef, Is.EqualTo("test"));
    }

    [Test]
    public void IngestIsIdempotent()
    {
        Package("a.osz", ("map.osu", Encoding.UTF8.GetBytes(Fixture("simple-120bpm.osu"))), ("audio.wav", wav));

        Ingest();
        var second = Ingest();

        Assert.That(second.SkippedPackages, Is.EqualTo(1));
        Assert.That(second.Accepted, Is.Zero);
        Assert.That(File.ReadAllLines(store.PathOf("manifests/maps.jsonl")), Has.Length.EqualTo(1));
    }

    [Test]
    public void MissingAudioIsQuarantinedWithoutGuessing()
    {
        Package("a.osz", ("map.osu", Encoding.UTF8.GetBytes(Fixture("simple-120bpm.osu"))), ("other.wav", wav));

        Ingest();

        var map = store.Maps.Single();
        Assert.That(map.Status, Is.EqualTo(IngestStatus.Quarantined));
        Assert.That(map.Reasons.Select(r => r.Code), Is.EqualTo(new[] { "audio.missing" }));
        Assert.That(Directory.GetFiles(store.PathOf("beatmaps")), Is.Empty);
    }

    [Test]
    public void AudioNameMatchesCaseInsensitivelyLikeTheGame()
    {
        Package("a.osz", ("map.osu", Encoding.UTF8.GetBytes(Fixture("simple-120bpm.osu"))), ("Audio.WAV", wav));

        Ingest();

        Assert.That(store.Maps.Single().Status, Is.EqualTo(IngestStatus.Accepted));
    }

    [Test]
    public void OtherRulesetsAreExcludedNotQuarantined()
    {
        string mania = Fixture("simple-120bpm.osu").Replace("Mode: 0", "Mode: 3");
        Package("a.osz", ("std.osu", Encoding.UTF8.GetBytes(Fixture("simple-120bpm.osu"))), ("mania.osu", Encoding.UTF8.GetBytes(mania)), ("audio.wav", wav));

        Ingest();

        Assert.That(store.Maps.Select(m => m.Status), Is.EquivalentTo(new[] { IngestStatus.Accepted, IngestStatus.Excluded }));
    }

    [Test]
    public void GeneratedMapsAreExcluded()
    {
        string generated = Fixture("simple-120bpm.osu").Replace("Tags:fixture synthetic", "Tags:osu-ai-mapper b1 generated");
        Package("a.osz", ("map.osu", Encoding.UTF8.GetBytes(generated)), ("audio.wav", wav));

        Ingest();

        var map = store.Maps.Single();
        Assert.That(map.Status, Is.EqualTo(IngestStatus.Excluded));
        Assert.That(map.Reasons.Single().Code, Is.EqualTo("generated.machine-made"));
    }

    [Test]
    public void UnsafePackageIsQuarantinedWhole()
    {
        Package("evil.osz", ("map.osu", Encoding.UTF8.GetBytes(Fixture("simple-120bpm.osu"))), ("../audio.wav", wav));

        var summary = Ingest();

        Assert.That(summary.Quarantined, Is.EqualTo(1));
        Assert.That(store.Maps, Is.Empty);
        Assert.That(store.Packages.Single().Reasons.Single().Code, Is.EqualTo("package.unsafe"));
    }

    [Test]
    public void AuditCatchesATamperedFile()
    {
        Package("a.osz", ("map.osu", Encoding.UTF8.GetBytes(Fixture("simple-120bpm.osu"))), ("audio.wav", wav));
        Ingest();
        SplitFiles.Save(store, Families.Freeze(store.Maps, null));

        Assert.That(Audit.Run(store), Is.Empty);

        File.AppendAllText(store.PathOf(store.Maps.Single().AudioRef!), "x");
        Assert.That(Audit.Run(store).Single(), Does.Contain("does not match its hash"));
    }

    [TestCase("Beauty and a Beat (Nightcore & Cut Ver.)", "Beauty and a Beat")]
    [TestCase("Song [TV Size]", "Song")]
    [TestCase("Song (Sped Up)", "Song")]
    [TestCase("Song feat. Someone", "Song")]
    [TestCase("Sóng!", "song")]
    public void TitleNormalisationDropsVersionMarkers(string title, string same) =>
        Assert.That(Families.NormaliseTitle(title), Is.EqualTo(Families.NormaliseTitle(same)));

    [Test]
    public void RemixIsADifferentComposition() =>
        Assert.That(Families.NormaliseTitle("Song (Remix)"), Is.Not.EqualTo(Families.NormaliseTitle("Song")));

    [TestCase("Justin Bieber, Nicki Minaj", "Nicki Minaj & Justin Bieber")]
    [TestCase("A feat. B", "B x A")]
    public void ArtistCreditsAreASet(string a, string b) =>
        Assert.That(Families.NormaliseArtist(a), Is.EqualTo(Families.NormaliseArtist(b)));

    [Test]
    public void VersionsOfOneSongShareAFamily()
    {
        string osu = Fixture("simple-120bpm.osu");
        Package("orig.osz", ("m.osu", Encoding.UTF8.GetBytes(WithMetadata(osu, "Song", "Artist A"))), ("audio.wav", wav));
        Package("nc.osz", ("m.osu", Encoding.UTF8.GetBytes(WithMetadata(osu, "Song (Nightcore Ver.)", "Artist A"))), ("audio.wav", [.. wav, 9]));
        Package("other.osz", ("m.osu", Encoding.UTF8.GetBytes(WithMetadata(osu, "Another Song", "Artist B"))), ("audio.wav", [.. wav, 8]));

        Ingest();
        var families = Families.Group(store.Maps);

        Assert.That(families.Select(f => f.MapSha256.Count).Order(), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void SharedAudioLinksDifferentMetadata()
    {
        string osu = Fixture("simple-120bpm.osu");
        Package("a.osz", ("m.osu", Encoding.UTF8.GetBytes(WithMetadata(osu, "Title One", "X"))), ("audio.wav", wav));
        Package("b.osz", ("m.osu", Encoding.UTF8.GetBytes(WithMetadata(osu, "Totally Other", "Y"))), ("audio.wav", wav));

        Ingest();

        Assert.That(Families.Group(store.Maps), Has.Count.EqualTo(1));
    }

    [Test]
    public void FrozenSplitKeepsAssignmentsAndRefusesCrossSplitMerges()
    {
        string osu = Fixture("simple-120bpm.osu");
        Package("a.osz", ("m.osu", Encoding.UTF8.GetBytes(WithMetadata(osu, "Song", "A"))), ("audio.wav", wav));
        Ingest();
        var v1 = Families.Freeze(store.Maps, null);

        // Pretend the frozen family was put in test, then add a second, unrelated family.
        var frozen = v1 with { Families = [v1.Families.Single() with { Split = Split.Test }] };
        Package("b.osz", ("m.osu", Encoding.UTF8.GetBytes(WithMetadata(osu, "Other", "B"))), ("audio.wav", [.. wav, 1]));
        Ingest();

        var extended = Families.Freeze(store.Maps, frozen);
        Assert.That(extended.Families.Single(f => f.Key.EndsWith("|song")).Split, Is.EqualTo(Split.Test));
        Assert.That(extended.Families, Has.Count.EqualTo(2));

        // A new package whose audio matches both frozen families would merge them across splits.
        var crossing = extended with { Families = [extended.Families[0] with { Split = Split.Train }, extended.Families[1] with { Split = Split.Test }] };
        Package("c.osz", ("m.osu", Encoding.UTF8.GetBytes(WithMetadata(osu, "Song", "A").Replace("Version:Simple", "Version:Other"))), ("audio.wav", [.. wav, 1]));
        Ingest();

        Assert.Throws<InvalidOperationException>(() => Families.Freeze(store.Maps, crossing));
    }

    [TestCase(new byte[] { 0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0, 0, 0 }, "mp3")]
    [TestCase(new byte[] { 0xFF, 0xFB, 0x90, 0x64, 0, 0, 0, 0, 0, 0, 0, 0 }, "mp3")]
    [TestCase(new byte[] { 0x4F, 0x67, 0x67, 0x53, 0, 2, 0, 0, 0, 0, 0, 0 }, "ogg")]
    [TestCase(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x64, 0x61, 0x73, 0x68 }, "m4a")]
    [TestCase(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, null)]
    public void SniffsAudioByContent(byte[] data, string? expected) => Assert.That(AudioSniffer.Container(data), Is.EqualTo(expected));
}
