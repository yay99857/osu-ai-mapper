using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OsuAi.BeatmapCore;
using OsuAi.Generation;
using OsuAi.OsuAdapter;
using OsuAi.ProjectStore;

namespace OsuAi.Application;

public enum PipelineStage
{
    PreparingAudio,
    Analysing,
    PredictingRhythm,
    Generating,
    RatingDifficulty,
    Validating,
    Saving,
    Exporting,
    SeparatingSources,
}

/// <summary>A real step of the pipeline, reported as it starts. No invented percentages.</summary>
/// <param name="Fraction">How much of the current step is done (0-1), when the step can tell.</param>
/// <param name="Remaining">How long the current step expects to still take, when it can tell.</param>
public sealed record PipelineProgress(PipelineStage Stage, string Message, double? Fraction = null, TimeSpan? Remaining = null);

public sealed record AnalysisOutcome(SongAnalysis Analysis, AnalysisRef Ref, bool Reused);

public sealed record GenerationOutcome(RevisionRecord Revision, BeatmapIr Beatmap, IReadOnlyList<Candidate> Candidates, IReadOnlyList<Diagnostic> Warnings);

/// <summary>
/// The operations a user performs on a project, shared by the CLI and the desktop app. Long
/// operations take a cancellation token and report their stages; a cancelled or failed operation
/// leaves the project as it was before it started.
/// </summary>
public sealed class MappingService(ToolEnvironment tools, string? sharedAnalysisCache = null)
{
    public const string GeneratedTag = "osu-ai-mapper";

    public Project CreateProject(string root, string audioPath, ProjectMetadata metadata, ProjectSettings settings)
    {
        if (!File.Exists(audioPath))
            throw new FileNotFoundException("Audio file not found.", audioPath);

        metadata = MetadataRules.Normalise(metadata);
        var problems = MetadataRules.Problems(metadata);

        if (problems.Count > 0)
            throw new InvalidMetadataException(problems);

        return Project.Create(root, audioPath, metadata, settings);
    }

    /// <summary>
    /// The cache key of an analysis: the original audio's bytes, the analyzer's code and the timing
    /// correction. Changing the song, the analyzer or the correction changes it; metadata does not.
    /// </summary>
    public string AnalysisKey(Project project) => KeyOf(project.Manifest.Original.Sha256, tools.AnalyzerId, project.Manifest.Settings.Timing);

    internal static string KeyOf(string audioSha256, string analyzerId, TimingCorrection? timing)
    {
        string correction = timing is null ? "detected" : $"bpm={timing.Bpm.ToString("R", CultureInfo.InvariantCulture)};offset={timing.OffsetMs?.ToString("R", CultureInfo.InvariantCulture) ?? "detected"}";
        string text = $"{audioSha256}|{analyzerId}|{correction}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24];
    }

    public async Task<AnalysisOutcome> AnalyseAsync(Project project, IProgress<PipelineProgress>? progress, CancellationToken cancellationToken)
    {
        string key = AnalysisKey(project);
        string relativeDir = $"analysis/{key}";
        string dir = project.PathOf(relativeDir);

        if (TryLoadValidated(dir, out var cached))
        {
            Use(project, key, relativeDir, cached!);
            return new AnalysisOutcome(cached!, project.Manifest.Analysis!, Reused: true);
        }

        // A shared cache (batch tools) can hold the same content-addressed analysis.
        if (sharedAnalysisCache is not null && TryLoadValidated(Path.Combine(sharedAnalysisCache, key), out var shared))
        {
            CopyDirectory(Path.Combine(sharedAnalysisCache, key), dir);
            Use(project, key, relativeDir, shared!);
            return new AnalysisOutcome(shared!, project.Manifest.Analysis!, Reused: true);
        }

        progress?.Report(new PipelineProgress(PipelineStage.PreparingAudio, "Decoding and transcoding the audio"));
        Directory.CreateDirectory(project.PathOf("analysis"));
        string staging = project.PathOf($"analysis/.staging-{Guid.NewGuid():N}");

        try
        {
            var arguments = new List<string> { "run", "--project", tools.AnalyzerProject, "osu-ai-analyze", project.PathOf(project.Manifest.Original.RelativePath), staging };

            if (project.Manifest.Settings.Timing is { } timing)
            {
                arguments.AddRange(["--bpm", timing.Bpm.ToString("R", CultureInfo.InvariantCulture)]);
                if (timing.OffsetMs is { } offset)
                    arguments.AddRange(["--offset-ms", offset.ToString("R", CultureInfo.InvariantCulture)]);
            }

            progress?.Report(new PipelineProgress(PipelineStage.Analysing, "Finding tempo, offset and sections"));
            var result = await ProcessRunner.RunAsync(tools.Uv, arguments, cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
                throw new AnalysisFailedException(LastLines(result.StandardError));

            if (!TryLoadValidated(staging, out var fresh))
                throw new AnalysisFailedException("The analyzer finished but its output does not match the audio it wrote.");

            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true); // an invalid leftover with this key
            Directory.Move(staging, dir);

            if (sharedAnalysisCache is not null && !Directory.Exists(Path.Combine(sharedAnalysisCache, key)))
                CopyDirectory(dir, Path.Combine(sharedAnalysisCache, key));

            Use(project, key, relativeDir, fresh!);
            return new AnalysisOutcome(fresh!, project.Manifest.Analysis!, Reused: false);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    /// <param name="preview">
    /// Map only the loudest 30 seconds (Mapperatorinator only): a few minutes instead of tens, to check
    /// stars and style before the whole song. The preview is a revision of its own, kept out of mapsets.
    /// </param>
    public async Task<GenerationOutcome> GenerateAsync(Project project, IProgress<PipelineProgress>? progress, CancellationToken cancellationToken, bool preview = false)
    {
        if (preview && !MapperatorinatorEngine.IsEngine(project.Manifest.Settings.RhythmModel))
            throw new InvalidOperationException("Previews are for Mapperatorinator; the other models generate the whole song in seconds.");
        var analysis = (await AnalyseAsync(project, progress, cancellationToken).ConfigureAwait(false)).Analysis;
        var settings = project.Manifest.Settings;
        var metadata = project.Manifest.Metadata;
        string key = project.Manifest.Analysis!.Key;

        if (MapperatorinatorEngine.IsEngine(settings.RhythmModel))
            return await GenerateWithMapperatorinatorAsync(project, analysis, preview, progress, cancellationToken).ConfigureAwait(false);

        RhythmPrediction? rhythm = null;
        string? modelSha = null;

        if (settings.RhythmModel is { } model)
        {
            if (NeedsStems(model))
                await SeparateAsync(project, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new PipelineProgress(PipelineStage.PredictingRhythm, "Choosing where to click (rhythm model)"));
            modelSha = Project.Sha256Of(model);
            rhythm = await PredictRhythmAsync(project, model, modelSha, settings.TargetStars, settings.Style ?? "auto", cancellationToken).ConfigureAwait(false);
        }

        string generator = rhythm?.Generator ?? "b1";
        var options = new GenerationOptions
        {
            TargetStars = settings.TargetStars,
            Seed = settings.Seed,
            AudioFileName = "audio.mp3",
            Metadata = ToBeatmapMetadata(metadata, generator),
            Rhythm = rhythm,
        };

        // Only what the user set overrides the generator's defaults.
        options = options with
        {
            Ar = settings.Ar ?? options.Ar,
            Od = settings.Od ?? options.Od,
            Cs = settings.Cs ?? options.Cs,
            Hp = settings.Hp ?? options.Hp,
        };

        progress?.Report(new PipelineProgress(PipelineStage.Generating, "Placing objects and searching spacing for the target"));
        var result = await Task.Run(() => B1Generator.Generate(analysis, options, map =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new PipelineProgress(PipelineStage.RatingDifficulty, "Rating a candidate with the official calculator"));
            return DifficultyService.Calculate(map).StarRating;
        }), cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new PipelineProgress(PipelineStage.Validating, "Validating and re-reading the encoded map"));
        var beatmap = result.Beatmap;
        string osuText = EncodeVerified(beatmap);
        var warnings = BeatmapValidator.Validate(beatmap).Concat(InterchangeProfile.Validate(beatmap)).ToArray();
        var difficulty = DifficultyService.CalculateFromOsuText(osuText);

        progress?.Report(new PipelineProgress(PipelineStage.Saving, "Saving the new revision"));
        var revision = project.AddRevision((staging, relative) =>
        {
            File.WriteAllText(Path.Combine(staging, "beatmap-ir.json"), IrJson.Serialize(beatmap));
            File.WriteAllText(Path.Combine(staging, "beatmap.osu"), osuText);

            return new RevisionRecord
            {
                Id = project.NextRevisionId,
                Parent = project.Manifest.ActiveRevision,
                Kind = RevisionKind.Generated,
                CreatedAt = DateTimeOffset.UtcNow,
                BeatmapIr = $"{relative}/beatmap-ir.json",
                AnalysisKey = key,
                Metadata = metadata,
                Generation = new GenerationRecord
                {
                    Generator = generator,
                    RhythmModelSha256 = modelSha,
                    Seed = settings.Seed,
                    TargetStars = settings.TargetStars,
                    ActualStars = difficulty.StarRating,
                    TargetReached = result.Status == GenerationStatus.Completed,
                    SpacingFactor = result.SpacingFactor,
                    DifficultyProfile = difficulty.Profile,
                },
            };
        });
        AddToPackage(project, revision);

        return new GenerationOutcome(revision, beatmap, result.Candidates, warnings);
    }

    /// <summary>
    /// The whole map from Mapperatorinator: our analysis gives the timing (a reference map built by
    /// the rules carries it in), the model writes rhythm and placement, and the result goes through
    /// the same validation, round trip and revision as our own generators.
    /// </summary>
    private async Task<GenerationOutcome> GenerateWithMapperatorinatorAsync(Project project, SongAnalysis analysis, bool preview,
        IProgress<PipelineProgress>? progress, CancellationToken cancellationToken)
    {
        if (!tools.MapperatorinatorAvailable)
            throw new GenerationFailedException($"Mapperatorinator is not installed in {tools.MapperatorinatorRoot}.");

        var settings = project.Manifest.Settings;
        var metadata = project.Manifest.Metadata;
        const string generator = "mapperatorinator";
        string version = settings.RhythmModel![MapperatorinatorEngine.ModelPrefix.Length..];
        (long StartMs, long EndMs)? span = preview ? PreviewSpan.Loudest(analysis) : null;
        if (preview)
            metadata = metadata with { DifficultyName = $"{metadata.DifficultyName} (prévia)" };
        double requested = StarCalibration.Request(settings.TargetStars, project.Manifest.StarObservations);

        var options = new GenerationOptions
        {
            TargetStars = settings.TargetStars,
            Seed = settings.Seed,
            AudioFileName = "audio.mp3",
            Metadata = ToBeatmapMetadata(metadata, generator),
        };
        options = options with { Ar = settings.Ar ?? options.Ar, Od = settings.Od ?? options.Od, Cs = settings.Cs ?? options.Cs, Hp = settings.Hp ?? options.Hp };

        string work = project.PathOf("work/mapperatorinator");
        if (Directory.Exists(work))
            Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        try
        {
            progress?.Report(new PipelineProgress(PipelineStage.Generating, "Preparando a timing e o áudio para o Mapperatorinator"));
            var reference = B1Generator.Build(analysis, RhythmSelection.Plan(analysis), options, 1.8);
            string referencePath = Path.Combine(work, "reference.osu");
            File.WriteAllText(referencePath, MapperatorinatorEngine.WholeNumberHitObjects(OsuExporter.ToOsuText(reference)));

            string wav = Path.Combine(work, "audio.wav");
            var convert = await ProcessRunner.RunAsync(tools.Uv,
                ["run", "--project", tools.AnalyzerProject, "osu-ai-to-wav", project.PathOf($"{project.Manifest.Analysis!.RelativePath}/audio.mp3"), wav],
                cancellationToken).ConfigureAwait(false);
            if (convert.ExitCode != 0)
                throw new GenerationFailedException("Audio conversion failed: " + LastLines(convert.StandardError));

            var request = new MapperatorinatorRequest(version, referencePath, wav, Path.Combine(work, "out"), requested, settings.Seed)
            {
                Span = span,
                Descriptors = settings.Descriptors,
                MapperId = settings.MapperId,
                StrongStyle = settings.StrongStyle,
                Year = settings.Year,
                CircleSize = settings.Cs,
            };
            string label = preview ? $"Prévia {MapReport.Clock(span!.Value.StartMs)}-{MapReport.Clock(span.Value.EndMs)}" : "Mapperatorinator";
            string osuText = await MapperatorinatorEngine.GenerateAsync(tools, request, progress, label, cancellationToken).ConfigureAwait(false);

            // Stretches it left empty while the music plays are generated again, with another seed,
            // and spliced in (at most three, so one stubborn passage cannot loop for ever).
            int attempt = 0;
            foreach (var (from, to) in MapperatorinatorEngine.EmptyStretches(osuText, analysis).Take(3))
            {
                attempt++;
                var patchRequest = request with
                {
                    OutputDir = Path.Combine(work, $"patch{attempt}"),
                    Seed = settings.Seed + 1000 * attempt,
                    Span = (Math.Max(0, from - 2000), to + 2000),
                };
                string patch = await MapperatorinatorEngine.GenerateAsync(tools, patchRequest, progress,
                    $"Refazendo o trecho vazio {MapReport.Clock(from)}-{MapReport.Clock(to)}", cancellationToken).ConfigureAwait(false);
                osuText = MapperatorinatorEngine.Splice(osuText, patch, from + 150, to - 150);
            }

            progress?.Report(new PipelineProgress(PipelineStage.Validating, "Conferindo o mapa"));
            var beatmap = MapperatorinatorEngine.Adopt(osuText, reference);
            double actual = DifficultyService.CalculateFromOsuText(EncodeVerified(beatmap)).StarRating;

            // AR, OD and HP do not change placement: what the user set, else the usual for the tier the
            // map landed in. CS went to the model, which places by it.
            var tier = DifficultyTiers.For(actual);
            beatmap = beatmap with
            {
                Settings = beatmap.Settings with
                {
                    Ar = settings.Ar ?? tier.Ar,
                    Od = settings.Od ?? tier.Od,
                    Hp = settings.Hp ?? tier.Hp,
                    PreviewTimeUs = span is { } p ? p.StartMs * 1000 : beatmap.Settings.PreviewTimeUs,
                },
            };
            string encoded = EncodeVerified(beatmap);
            var warnings = BeatmapValidator.Validate(beatmap).Concat(InterchangeProfile.Validate(beatmap)).ToArray();
            var difficulty = DifficultyService.CalculateFromOsuText(encoded);

            progress?.Report(new PipelineProgress(PipelineStage.Saving, "Saving the new revision"));
            string key = project.Manifest.Analysis!.Key;
            var revision = project.AddRevision((staging, relative) =>
            {
                File.WriteAllText(Path.Combine(staging, "beatmap-ir.json"), IrJson.Serialize(beatmap));
                File.WriteAllText(Path.Combine(staging, "beatmap.osu"), encoded);
                return new RevisionRecord
                {
                    Id = project.NextRevisionId,
                    Parent = project.Manifest.ActiveRevision,
                    Kind = RevisionKind.Generated,
                    CreatedAt = DateTimeOffset.UtcNow,
                    BeatmapIr = $"{relative}/beatmap-ir.json",
                    AnalysisKey = key,
                    Metadata = metadata,
                    Generation = new GenerationRecord
                    {
                        Generator = generator,
                        RhythmModelSha256 = null,
                        Seed = settings.Seed,
                        TargetStars = settings.TargetStars,
                        ActualStars = difficulty.StarRating,
                        TargetReached = Math.Abs(difficulty.StarRating - settings.TargetStars) <= 0.5,
                        SpacingFactor = 1,
                        DifficultyProfile = difficulty.Profile,
                        RequestedStars = Math.Abs(requested - settings.TargetStars) > 0.001 ? requested : null,
                        PreviewStartMs = span?.StartMs,
                        PreviewEndMs = span?.EndMs,
                    },
                };
            });
            project.Update(m => m with
            {
                StarObservations = [.. m.StarObservations.TakeLast(9), new StarObservation(requested, difficulty.StarRating, preview, DateTimeOffset.UtcNow)],
            });
            AddToPackage(project, revision);

            return new GenerationOutcome(revision, beatmap, [], warnings);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // A file still held open; the next run clears the folder first.
            }
        }
    }

    /// <summary>One difficulty of a mapset: its name in osu! and its star target.</summary>
    public sealed record MapsetDifficulty(string Name, double Stars);

    /// <summary>
    /// Generates several difficulties one after another with the current model and style, each a
    /// revision in the package, so the next export is a whole mapset. The form's own target and
    /// difficulty name are put back afterwards. Cancelling keeps the difficulties already finished.
    /// </summary>
    public async Task<IReadOnlyList<GenerationOutcome>> GenerateMapsetAsync(Project project, IReadOnlyList<MapsetDifficulty> difficulties,
        IProgress<PipelineProgress>? progress, CancellationToken cancellationToken)
    {
        if (difficulties.Count == 0)
            throw new ArgumentException("Choose at least one difficulty.");
        if (difficulties.Select(d => d.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != difficulties.Count)
            throw new ArgumentException("Difficulty names in a mapset must differ.");

        var settings = project.Manifest.Settings;
        var metadata = project.Manifest.Metadata;
        var outcomes = new List<GenerationOutcome>();
        // What is exported now stays in the set; each new difficulty joins it (or replaces its namesake).
        project.Update(m => m with { Package = PackageOf(project).Where(r => !r.Generation.IsPreview).Select(r => r.Id).ToArray() });
        try
        {
            for (int i = 0; i < difficulties.Count; i++)
            {
                var d = difficulties[i];
                project.Update(m => m with
                {
                    Settings = m.Settings with { TargetStars = d.Stars },
                    Metadata = m.Metadata with { DifficultyName = d.Name.Trim() },
                });
                var step = progress is null ? null : new Progress<PipelineProgress>(p =>
                    progress.Report(p with { Message = $"[{i + 1}/{difficulties.Count} {d.Name}] {p.Message}" }));
                outcomes.Add(await GenerateAsync(project, step, cancellationToken).ConfigureAwait(false));
            }
        }
        finally
        {
            project.Update(m => m with
            {
                Settings = m.Settings with { TargetStars = settings.TargetStars },
                Metadata = m.Metadata with { DifficultyName = metadata.DifficultyName },
            });
        }
        return outcomes;
    }

    /// <summary>The revisions the next export packages: one per difficulty name, the active one always.</summary>
    public static IReadOnlyList<RevisionRecord> PackageOf(Project project)
    {
        var active = project.ActiveRevision;
        var chosen = (project.Manifest.Package ?? [])
            .Select(id => project.Manifest.Revisions.FirstOrDefault(r => r.Id == id))
            .OfType<RevisionRecord>()
            .Where(r => !r.Generation.IsPreview && r.Id != active?.Id)
            .ToList();
        if (active is not null)
            chosen.Insert(0, active);
        return chosen.DistinctBy(r => r.Metadata.DifficultyName.Trim(), StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Puts a revision into the package or takes it out; the active revision cannot leave it.</summary>
    public void SetInPackage(Project project, string revisionId, bool include)
    {
        var revision = project.Manifest.Revisions.FirstOrDefault(r => r.Id == revisionId)
            ?? throw new ArgumentException($"No revision {revisionId}.");
        if (!include && revisionId == project.Manifest.ActiveRevision)
            throw new InvalidOperationException("A revisão ativa sempre vai no pacote.");
        if (include && revision.Generation.IsPreview)
            throw new InvalidOperationException("Uma prévia não entra no mapset; gere o mapa inteiro.");

        var ids = PackageOf(project).Select(r => r.Id).ToList();
        if (include)
        {
            // One difficulty name per package: the new one replaces another of the same name.
            ids.RemoveAll(id => project.Manifest.Revisions.First(r => r.Id == id).Metadata.DifficultyName.Trim()
                .Equals(revision.Metadata.DifficultyName.Trim(), StringComparison.OrdinalIgnoreCase) && id != project.Manifest.ActiveRevision);
            ids.Add(revisionId);
        }
        else
        {
            ids.Remove(revisionId);
        }
        project.Update(m => m with { Package = ids });
    }

    /// <summary>
    /// Once the user has built a package (a mapset), a new revision joins it in place of any other
    /// revision with its difficulty name, and a metadata revision in place of the one it renames.
    /// Without a package only the active revision is exported, as before mapsets. Previews stay out.
    /// </summary>
    private static void AddToPackage(Project project, RevisionRecord revision)
    {
        if (revision.Generation.IsPreview || project.Manifest.Package is not { } package)
            return;
        string name = revision.Metadata.DifficultyName.Trim();
        string? replaced = revision.Kind == RevisionKind.Metadata ? revision.Parent : null;
        var ids = package
            .Where(id => id != replaced)
            .Where(id => project.Manifest.Revisions.FirstOrDefault(r => r.Id == id) is { } r
                         && !r.Generation.IsPreview
                         && !r.Metadata.DifficultyName.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            .Append(revision.Id)
            .ToList();
        project.Update(m => m with { Package = ids });
    }

    /// <summary>The checks of <see cref="MapReport"/> on a revision, with the analysis it was made from.</summary>
    public static IReadOnlyList<MapIssue> ReportOf(Project project, RevisionRecord revision)
    {
        var beatmap = IrJson.Deserialize<BeatmapIr>(File.ReadAllText(project.PathOf(revision.BeatmapIr)));
        string analysisFile = project.PathOf($"analysis/{revision.AnalysisKey}/analysis.json");
        if (!File.Exists(analysisFile))
            return [];
        var analysis = SongAnalysis.Load(analysisFile);
        var g = revision.Generation;
        return MapReport.Check(beatmap, analysis, g.TargetStars, g.ActualStars,
            g.PreviewStartMs is { } from && g.PreviewEndMs is { } to ? (from, to) : null);
    }

    public static readonly IReadOnlyList<string> BackgroundExtensions = [".jpg", ".jpeg", ".png"];

    /// <summary>
    /// Sets the background image packaged with the map (JPG or PNG, at most 20 MB). It applies on the
    /// next export; nothing is generated again.
    /// </summary>
    public ManagedFile SetBackground(Project project, string imagePath)
    {
        if (!BackgroundExtensions.Contains(Path.GetExtension(imagePath).ToLowerInvariant()))
            throw new ArgumentException("A imagem de fundo precisa ser JPG ou PNG.");
        if (new FileInfo(imagePath).Length > 20 * 1024 * 1024)
            throw new ArgumentException("A imagem de fundo tem mais de 20 MB.");
        return project.SetBackground(imagePath);
    }

    public void ClearBackground(Project project) => project.ClearBackground();

    /// <summary>Saves metadata for the next generation, without creating a revision.</summary>
    public ProjectMetadata SetMetadata(Project project, ProjectMetadata metadata)
    {
        metadata = MetadataRules.Normalise(metadata);
        var problems = MetadataRules.Problems(metadata);

        if (problems.Count > 0)
            throw new InvalidMetadataException(problems);

        project.Update(m => m with { Metadata = metadata });
        return metadata;
    }

    /// <summary>
    /// New metadata for the project. With a revision, its objects and timing are kept as they are and
    /// a metadata revision is added; nothing is analysed or generated again.
    /// </summary>
    public RevisionRecord? UpdateMetadata(Project project, ProjectMetadata metadata)
    {
        metadata = SetMetadata(project, metadata);

        if (project.ActiveRevision is not { } active)
            return null;

        var beatmap = IrJson.Deserialize<BeatmapIr>(File.ReadAllText(project.PathOf(active.BeatmapIr)));
        beatmap = beatmap with
        {
            RevisionId = Guid.NewGuid(),
            ParentRevisionId = beatmap.RevisionId,
            Metadata = ToBeatmapMetadata(metadata, active.Generation.Generator),
        };
        string osuText = EncodeVerified(beatmap);

        var revision = project.AddRevision((staging, relative) =>
        {
            File.WriteAllText(Path.Combine(staging, "beatmap-ir.json"), IrJson.Serialize(beatmap));
            File.WriteAllText(Path.Combine(staging, "beatmap.osu"), osuText);

            return active with
            {
                Id = project.NextRevisionId,
                Parent = active.Id,
                Kind = RevisionKind.Metadata,
                CreatedAt = DateTimeOffset.UtcNow,
                BeatmapIr = $"{relative}/beatmap-ir.json",
                Metadata = metadata,
            };
        });
        AddToPackage(project, revision);
        return revision;
    }

    public void UpdateSettings(Project project, Func<ProjectSettings, ProjectSettings> change) =>
        project.Update(m => m with { Settings = change(m.Settings) });

    /// <summary>
    /// Packages the revisions of the package (<see cref="PackageOf"/>: the active one, plus the other
    /// difficulties of a mapset) with the audio and the background into exports/, and optionally
    /// copies the package elsewhere. Song metadata comes from the active revision for every
    /// difficulty, so a renamed title reaches them all; each keeps its own difficulty name.
    /// </summary>
    public string Export(Project project, string? copyTo = null, IProgress<PipelineProgress>? progress = null)
    {
        var active = project.ActiveRevision ?? throw new InvalidOperationException("Nothing to export yet: generate a beatmap first.");
        var analysis = project.Manifest.Analysis ?? throw new InvalidOperationException("The project has no analysed audio.");

        progress?.Report(new PipelineProgress(PipelineStage.Exporting, "Writing the .osz package"));
        // The background is a project asset, applied at export: changing it needs no new revision.
        string? background = project.Manifest.Background is { } bg ? project.PathOf(bg.RelativePath) : null;
        var song = IrJson.Deserialize<BeatmapIr>(File.ReadAllText(project.PathOf(active.BeatmapIr))).Metadata;

        var maps = new List<byte[]>();
        foreach (var revision in PackageOf(project))
        {
            var beatmap = IrJson.Deserialize<BeatmapIr>(File.ReadAllText(project.PathOf(revision.BeatmapIr)));
            beatmap = beatmap with
            {
                Metadata = song with { DifficultyName = beatmap.Metadata.DifficultyName, Tags = beatmap.Metadata.Tags.Union(song.Tags).ToArray() },
                Assets = beatmap.Assets with
                {
                    Background = background is null ? null : new AssetRef { Id = "background", RelativePath = "background" + Path.GetExtension(background).ToLowerInvariant() },
                },
            };
            maps.Add(Encoding.UTF8.GetBytes(EncodeVerified(beatmap)));
        }
        string audio = project.PathOf($"{analysis.RelativePath}/audio.mp3");

        // A single difficulty keeps its name in the package's; a mapset is named after the song.
        string name = maps.Count == 1
            ? Path.ChangeExtension(PackageNames.OsuFileName(song with { DifficultyName = active.Metadata.DifficultyName }), ".osz")
            : PackageNames.SetFileName(song);
        Directory.CreateDirectory(project.PathOf("exports"));
        string package = project.PathOf($"exports/{name}");
        OszPackager.PackSet(maps, audio, package, background);

        if (copyTo is null)
            return package;

        Directory.CreateDirectory(copyTo);
        string copy = Path.Combine(copyTo, name);
        File.Copy(package, copy, overwrite: true);
        return copy;
    }

    /// <summary>
    /// Whether a model reads separated stems: its card (model.card.json beside it) says "needsStems".
    /// Models without a card, B2 and Pulso v2-v4, do not.
    /// </summary>
    public static bool NeedsStems(string model)
    {
        string card = Path.ChangeExtension(model, ".card.json");
        if (!File.Exists(card))
            return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(card));
            return doc.RootElement.TryGetProperty("needsStems", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Demucs stems (voice, drums, bass, the rest) next to the analysis, once per analysis: about a
    /// minute and a half of CPU for a song. The rhythm predictor reads stems.json from there.
    /// </summary>
    private async Task SeparateAsync(Project project, IProgress<PipelineProgress>? progress, CancellationToken cancellationToken)
    {
        string dir = project.PathOf(project.Manifest.Analysis!.RelativePath);
        string stems = Path.Combine(dir, "stems.json");
        if (File.Exists(stems))
            return;

        progress?.Report(new PipelineProgress(PipelineStage.SeparatingSources, "Separando voz e instrumentos (Demucs, cerca de 1 a 2 minutos)"));
        string temp = $"{stems}.{Guid.NewGuid():N}.tmp";
        try
        {
            var result = await ProcessRunner.RunAsync(tools.Uv,
                ["run", "--project", tools.SeparationProject, "osu-ai-separate", Path.Combine(dir, "audio.mp3"), Path.Combine(dir, "analysis.json"), temp],
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new AnalysisFailedException("Source separation failed: " + LastLines(result.StandardError));
            File.Move(temp, stems, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private async Task<RhythmPrediction> PredictRhythmAsync(Project project, string model, string modelSha, double stars, string style, CancellationToken cancellationToken)
    {
        if (style != "auto" && !ProjectSettings.Styles.Contains(style))
            throw new ArgumentException($"Unknown style '{style}'.", nameof(style));

        // Predictions are cached next to their analysis, keyed by model, inference code, target stars and style.
        string relative = $"{project.Manifest.Analysis!.RelativePath}/rhythm-{modelSha[..12]}-{tools.RhythmToolId["rhythm-".Length..][..8]}-{stars.ToString("0.###", CultureInfo.InvariantCulture)}-{style}.json";
        string path = project.PathOf(relative);

        if (File.Exists(path))
        {
            var cached = RhythmPrediction.Load(path);
            if (cached.ModelSha256 == modelSha)
                return cached;
        }

        string temp = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            // Pulso-S (.pt) runs in the PyTorch environment; the other models in ml/rhythm.
            bool sequence = string.Equals(Path.GetExtension(model), ".pt", StringComparison.OrdinalIgnoreCase);
            var result = await ProcessRunner.RunAsync(tools.Uv,
                ["run", "--project", sequence ? tools.SequenceProject : tools.RhythmProject, sequence ? "osu-ai-sequence" : "osu-ai-rhythm", "predict", model, project.PathOf($"{project.Manifest.Analysis.RelativePath}/analysis.json"),
                 stars.ToString("R", CultureInfo.InvariantCulture), temp, style], cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
                throw new AnalysisFailedException("Rhythm model failed: " + LastLines(result.StandardError));

            File.Move(temp, path, overwrite: true);
            return RhythmPrediction.Load(path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static BeatmapMetadata ToBeatmapMetadata(ProjectMetadata m, string generator) => new()
    {
        Title = m.Title,
        TitleUnicode = m.TitleUnicode,
        Artist = m.Artist,
        ArtistUnicode = m.ArtistUnicode,
        Creator = m.Creator,
        DifficultyName = m.DifficultyName,
        Source = m.Source,
        // The generated marker is always present so a generated map can never pass for a human one
        // (the dataset builder excludes it); the user's own tags follow.
        Tags = [GeneratedTag, generator, .. m.Tags.Where(t => t != GeneratedTag && t != generator)],
    };

    /// <summary>Encodes with the official encoder and re-reads the text, refusing anything that does not come back the same.</summary>
    private static string EncodeVerified(BeatmapIr beatmap)
    {
        string text = OsuExporter.ToOsuText(beatmap);
        var reread = OsuImporter.Import(Encoding.UTF8.GetBytes(text));
        var differences = SemanticComparer.Compare(beatmap, reread.Beatmap);

        if (differences.Count > 0)
            throw new InvalidOperationException($"The encoded map differs from the generated one: {differences[0]}");

        return text;
    }

    private static bool TryLoadValidated(string dir, out SongAnalysis? analysis)
    {
        analysis = null;
        string json = Path.Combine(dir, "analysis.json");
        string audio = Path.Combine(dir, "audio.mp3");

        if (!File.Exists(json) || !File.Exists(audio))
            return false;

        try
        {
            var loaded = SongAnalysis.Load(json);

            // The analysis must describe exactly the audio stored beside it.
            if (loaded.Audio.Sha256 != Project.Sha256Of(audio))
                return false;

            analysis = loaded;
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private void Use(Project project, string key, string relativeDir, SongAnalysis analysis)
    {
        string audioRelative = $"{relativeDir}/audio.mp3";
        project.Update(m => m with
        {
            Analysis = new AnalysisRef(key, relativeDir, tools.AnalyzerId, m.Settings.Timing),
            Audio = new ManagedFile(audioRelative, analysis.Audio.Sha256, new FileInfo(project.PathOf(audioRelative)).Length),
        });
    }

    private static void CopyDirectory(string from, string to)
    {
        string staging = $"{to}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(staging);

        foreach (string file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(staging, Path.GetFileName(file)));

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);

        if (Directory.Exists(to))
            Directory.Delete(staging, recursive: true);
        else
            Directory.Move(staging, to);
    }

    private static string LastLines(string text) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(8));
}

public sealed class AnalysisFailedException(string message) : Exception(message);

public sealed class GenerationFailedException(string message) : Exception(message);
