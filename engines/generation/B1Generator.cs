using OsuAi.BeatmapCore;

namespace OsuAi.Generation;

public sealed record GenerationOptions
{
    public double TargetStars { get; init; } = 5.0;
    public double Tolerance { get; init; } = 0.3;
    public double Ar { get; init; } = 9.3;
    public double Od { get; init; } = 8;
    public double Cs { get; init; } = 4;
    public double Hp { get; init; } = 5;
    public double SliderMultiplier { get; init; } = 1.8;
    public int Seed { get; init; } = 7319;

    /// <summary>
    /// Shift from the acoustic attack to where osu! red lines go. osu!lazer adds 15 ms on Windows
    /// (FramedBeatmapClock.WINDOWS_BASE_AUDIO_OFFSET) so maps timed in stable stay in sync, and human
    /// maps carry that convention: measured on held-out human maps, -15 ms leaves a median error of
    /// about 5 ms. The analysis stays acoustic; only the written map follows the convention.
    /// </summary>
    public long ClientConventionUs { get; init; } = -15_000;

    /// <summary>Upper bound on candidates rated by the official calculator (blueprint ADR-014).</summary>
    public int MaxCandidates { get; init; } = 8;

    public required BeatmapMetadata Metadata { get; init; }

    /// <summary>When set, clicks come from the learned rhythm model (B2) instead of the B1 heuristics.</summary>
    public RhythmPrediction? Rhythm { get; init; }
    public required string AudioFileName { get; init; }
}

public enum GenerationStatus
{
    Completed,

    /// <summary>A valid map was produced but the star target was not reached within the budget.</summary>
    Infeasible,
}

public sealed record Candidate(double SpacingFactor, double Stars);

public sealed record GenerationResult(
    BeatmapIr Beatmap,
    GenerationStatus Status,
    double Stars,
    double SpacingFactor,
    IReadOnlyList<Candidate> Candidates,
    MappingPlan Plan);

/// <summary>
/// Baseline B1: rhythm selection from audio features, procedural placement, and a bounded spacing
/// search against the official star rating. Stars come in through <paramref name="rateStars"/> so
/// this engine never depends on the osu! adapter.
/// </summary>
public static class B1Generator
{
    public static GenerationResult Generate(SongAnalysis analysis, GenerationOptions options, Func<BeatmapIr, double> rateStars)
    {
        var tier = RhythmSelection.TierFor(options.TargetStars);
        var plan = options.Rhythm is { } rhythm
            ? RhythmSelection.PlanFromModel(analysis, tier, rhythm)
            : RhythmSelection.Plan(analysis, tier);

        if (plan.Events.Count == 0)
            throw new InvalidOperationException("No events were selected; the audio may be silent or outside the supported domain.");

        var candidates = new List<(Candidate Candidate, BeatmapIr Map)>();
        // With learned distances the factor is a scale on what human mappers do (1 = as learned),
        // applied mostly to loud passages (ProceduralPlacement.LoudnessScaled): at the 1.2 ceiling
        // calm parts move 4% and choruses 28%, about the chorus jumps of popular Insane maps (median
        // 5 radii). A 1.5 ceiling put Hot Together's chorus at 7.5 radii, beyond human maps of
        // its stars. A target still out of reach is reported as such.
        bool learned = plan.Events.Any(e => e.DistanceRadii is not null);
        double low = learned ? 0.85 : 1.0, high = learned ? 1.2 : options.Rhythm?.Notes is null ? 3.4 : 4.8;

        for (int i = 0; i < options.MaxCandidates; i++)
        {
            double factor = i == 0 ? (learned ? 1.0 : 1.8) : (low + high) / 2;
            var map = Build(analysis, plan, options, factor);
            double stars = rateStars(map);
            candidates.Add((new Candidate(factor, stars), map));

            if (Math.Abs(stars - options.TargetStars) <= options.Tolerance / 3)
                break;

            // Star rating rises with spacing; bisect towards the target.
            if (stars < options.TargetStars)
                low = factor;
            else
                high = factor;
        }

        var best = candidates.MinBy(c => Math.Abs(c.Candidate.Stars - options.TargetStars));
        var status = Math.Abs(best.Candidate.Stars - options.TargetStars) <= options.Tolerance ? GenerationStatus.Completed : GenerationStatus.Infeasible;

        return new GenerationResult(best.Map, status, best.Candidate.Stars, best.Candidate.SpacingFactor, candidates.Select(c => c.Candidate).ToArray(), plan);
    }

    public static BeatmapIr Build(SongAnalysis analysis, MappingPlan plan, GenerationOptions options, double spacingFactor)
    {
        var placed = ProceduralPlacement.Place(plan, new PlacementSettings
        {
            CircleSize = options.Cs,
            SliderMultiplier = options.SliderMultiplier,
            SpacingFactor = spacingFactor,
            Seed = options.Seed,
            Tier = RhythmSelection.TierFor(options.TargetStars),
        }, analysis.Timing.BeatLengthUs);

        var timing = new TimingMap
        {
            Segments =
            [
                new TimingSegment
                {
                    Id = "t0",
                    StartTimeUs = analysis.Timing.OffsetUs + options.ClientConventionUs,
                    StartBeat = Rational.Zero,
                    BeatLengthUs = analysis.Timing.BeatLengthUs,
                    Meter = new Meter(4, 4),
                    Source = analysis.Timing.IsManual ? TimingSource.Manual : TimingSource.Detected,
                    Confidence = Math.Clamp(analysis.Timing.Confidence, 0, 1),
                },
            ],
        };

        var objects = new List<HitObjectIr>(placed.Count);
        string generator = options.Rhythm?.Generator ?? "b1";

        foreach (var p in placed)
        {
            var e = p.Event;
            var id = DeterministicGuid.Create($"{analysis.Audio.Sha256}:b1:{options.Seed}:{e.GridIndex}");
            long timeUs = (long)Math.Round(timing.TimeUsAt(new Rational(e.GridIndex, RhythmSelection.GridPerBeat)));
            var position = new Vector2D(Math.Round(p.Position.X), Math.Round(p.Position.Y));
            var samples = Samples(e, p.NewCombo);

            if (p.Path is { } path)
            {
                objects.Add(new SliderIr
                {
                    Id = id,
                    TimeUs = timeUs,
                    Position = position,
                    NewCombo = p.NewCombo,
                    Samples = samples,
                    Path = path,
                    ExpectedDistancePx = Math.Round(p.SliderLengthPx, 2),
                    SpanCount = p.SpanCount,
                    SvMultiplier = Math.Round(p.Velocity, 3),
                    NodeSamples = [samples, .. Enumerable.Range(0, p.SpanCount).Select(_ => (IReadOnlyList<HitSample>)[Normal()])],
                    Origin = generator,
                });
            }
            else
            {
                objects.Add(new CircleIr { Id = id, TimeUs = timeUs, Position = position, NewCombo = p.NewCombo, Samples = samples, Origin = generator });
            }
        }

        objects = WithSpinners(objects, analysis, timing, options, generator);
        string sha = analysis.Audio.Sha256;

        return new BeatmapIr
        {
            Id = DeterministicGuid.Create($"{sha}:b1:beatmap"),
            RevisionId = DeterministicGuid.Create($"{sha}:b1:{options.Seed}:{spacingFactor:R}"),
            Metadata = options.Metadata,
            Assets = new BeatmapAssets { Audio = new AssetRef { Id = "audio", RelativePath = options.AudioFileName, Sha256 = null, MimeType = "audio/mpeg" } },
            Settings = new DifficultySettings
            {
                Ar = options.Ar,
                Od = options.Od,
                Cs = options.Cs,
                Hp = options.Hp,
                SliderMultiplier = options.SliderMultiplier,
                SliderTickRate = 1,
                PreviewTimeUs = PreviewTime(plan, options.ClientConventionUs),
            },
            Timing = timing,
            Controls = new BeatmapControls { Effects = Kiai(analysis, plan, options.ClientConventionUs) },
            Breaks = Breaks(objects, analysis, options),
            Objects = objects,
            Provenance = new Provenance
            {
                CreatedBy = options.Rhythm is { } r
                    ? $"generation.{r.Generator} rhythm-model={r.ModelSha256[..12]} seed={options.Seed} spacing={spacingFactor:0.###}"
                    : $"generation.b1 seed={options.Seed} spacing={spacingFactor:0.###}",
            },
        };
    }

    private static HitSample Normal() => new() { Name = HitSample.Normal, Bank = "soft", Volume = 60 };

    /// <summary>Clap on beats two and four in busy phrases, finish on phrase-opening downbeats.</summary>
    private static HitSample[] Samples(PlannedEvent e, bool newCombo)
    {
        var list = new List<HitSample> { Normal() };
        int beatInBar = e.GridIndex % RhythmSelection.GridPerBar / RhythmSelection.GridPerBeat;
        int bar = RhythmSelection.Bar(e.GridIndex);

        if (e.Intensity >= 2 && e.IsBeat && beatInBar is 1 or 3)
            list.Add(new HitSample { Name = HitSample.Clap, Bank = "soft", Volume = 60, AutoBank = true });

        if (e.IsDownbeat && bar % 4 == 0 && (newCombo || e.Intensity >= 3))
            list.Add(new HitSample { Name = HitSample.Finish, Bank = "soft", Volume = 60, AutoBank = true });

        return list.ToArray();
    }

    private static EffectControl[] Kiai(SongAnalysis analysis, MappingPlan plan, long conventionUs)
    {
        var effects = new List<EffectControl>();
        bool on = false;

        foreach (var bar in plan.PhraseIntensityByBar.Keys.Order())
        {
            bool peak = plan.PhraseIntensityByBar[bar] >= 3;

            if (peak == on)
                continue;

            on = peak;
            long timeUs = (long)Math.Round(analysis.Timing.OffsetUs + conventionUs + bar * RhythmSelection.GridPerBar / (double)RhythmSelection.GridPerBeat * analysis.Timing.BeatLengthUs);
            effects.Add(new EffectControl(Math.Max(0, timeUs), on));
        }

        return effects.ToArray();
    }

    private static long? PreviewTime(MappingPlan plan, long conventionUs)
    {
        // The legacy format stores whole milliseconds.
        var peak = plan.Events.FirstOrDefault(e => e.Intensity >= 3 && e.IsDownbeat) ?? plan.Events[0];
        return Math.Max(0, (long)Math.Round((peak.TimeUs + conventionUs) / 1000.0) * 1000);
    }

    /// <summary>
    /// Spinners where human maps put them. Of 1,197 human Insane and Expert maps (training split)
    /// 53% have one, about one per map, typically six beats long (3-16), over sound at 0.76 of the
    /// song's median loudness; half are followed by objects within a beat (a build-up spun through
    /// into a drop) and a quarter end the song. So: before the song's biggest drop, one or two
    /// calm bars become a spinner ending a beat before the drop; and audible music after the last
    /// object (three beats or more) gets a spinner of up to sixteen beats. Objects inside a
    /// spinner's span, and within half a beat of it, give way.
    /// </summary>
    internal static List<HitObjectIr> WithSpinners(List<HitObjectIr> objects, SongAnalysis analysis, TimingMap timing, GenerationOptions options, string generator)
    {
        if (objects.Count < 32)
            return objects;

        var grid = analysis.Grid.Where(p => p.Index >= 0).OrderBy(p => p.Index).ToArray();
        if (grid.Length < 64)
            return objects;
        double median = grid.Select(p => p.Energy).Order().ElementAt(grid.Length / 2);
        long At(int index) => (long)Math.Round(timing.TimeUsAt(new Rational(index, RhythmSelection.GridPerBeat)));
        var spans = new List<(int Start, int End)>();

        // Before the biggest drop.
        var bars = grid.GroupBy(p => p.Index / RhythmSelection.GridPerBar).Where(b => b.Count() >= 12)
                       .ToDictionary(b => b.Key, b => b.Average(p => p.Energy));
        double high = bars.Values.Order().ElementAt((int)(bars.Count * 0.6));
        int firstHead = (int)Math.Round((objects[0].TimeUs - timing.Segments[0].StartTimeUs) / (analysis.Timing.BeatLengthUs / RhythmSelection.GridPerBeat));
        var drop = bars.Keys.Where(b => b >= 4 && Enumerable.Range(b - 4, 4).All(bars.ContainsKey))
                       .Select(b => (Bar: b, Before: Enumerable.Range(b - 4, 4).Average(k => bars[k]), Energy: bars[b]))
                       .Where(d => d.Before > 0.05 && d.Energy > 1.6 * d.Before && d.Energy >= high)
                       .Where(d => d.Bar * RhythmSelection.GridPerBar - 2 * RhythmSelection.GridPerBar > firstHead + 4 * RhythmSelection.GridPerBar)
                       .OrderByDescending(d => d.Energy / d.Before).FirstOrDefault();
        if (drop.Bar > 0)
        {
            int downbeat = drop.Bar * RhythmSelection.GridPerBar;
            foreach (int barsBack in new[] { 2, 1 })
            {
                int start = downbeat - barsBack * RhythmSelection.GridPerBar;
                double calm = Enumerable.Range(drop.Bar - barsBack, barsBack).Where(bars.ContainsKey).DefaultIfEmpty(drop.Bar).Average(b => bars.GetValueOrDefault(b, double.MaxValue));
                if (calm <= median)
                {
                    spans.Add((start, downbeat - RhythmSelection.GridPerBeat));
                    break;
                }
            }
        }

        // After the last object, while the music can still be heard.
        long lastEnd = objects.Max(o => o switch
        {
            SliderIr s => s.TimeUs + (long)Math.Round(s.SpanCount * s.ExpectedDistancePx!.Value / (options.SliderMultiplier * 100 * s.SvMultiplier) * analysis.Timing.BeatLengthUs),
            _ => o.TimeUs,
        });
        var tail = grid.FirstOrDefault(p => At(p.Index) >= lastEnd);
        int afterLast = (tail?.Index ?? int.MaxValue / 2) + RhythmSelection.GridPerBeat;
        int lastAudible = grid.Where(p => p.Energy >= 0.5 * median).Select(p => p.Index).DefaultIfEmpty(0).Max();
        if (tail is not null && lastAudible - afterLast >= 3 * RhythmSelection.GridPerBeat)
            spans.Add((afterLast, Math.Min(lastAudible, afterLast + 16 * RhythmSelection.GridPerBeat)));

        if (spans.Count == 0)
            return objects;

        long margin = (long)(analysis.Timing.BeatLengthUs / 2);
        var kept = objects.Where(o => !spans.Any(s => o.TimeUs >= At(s.Start) - margin && o.TimeUs <= At(s.End) + margin)).ToList();
        foreach (var (start, end) in spans)
        {
            kept.Add(new SpinnerIr
            {
                Id = DeterministicGuid.Create($"{analysis.Audio.Sha256}:spinner:{options.Seed}:{start}"),
                TimeUs = At(start),
                EndTimeUs = At(end),
                Position = new Vector2D(256, 192),
                NewCombo = true,
                Samples = [Normal()],
                Origin = generator,
            });
        }

        // osu! always starts a new combo after a spinner; the map must say so to survive a round trip.
        var ordered = kept.OrderBy(o => o.TimeUs).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i - 1] is SpinnerIr && !ordered[i].NewCombo)
                ordered[i] = ordered[i] with { NewCombo = true };
        }

        return ordered;
    }

    /// <summary>Breaks in gaps of at least five seconds, leaving room before the next object fades in.</summary>
    private static BreakPeriod[] Breaks(IReadOnlyList<HitObjectIr> objects, SongAnalysis analysis, GenerationOptions options)
    {
        double preemptMs = options.Ar < 5 ? 1200 + 600 * (5 - options.Ar) / 5 : 1200 - 750 * (options.Ar - 5) / 5;
        var breaks = new List<BreakPeriod>();

        for (int i = 1; i < objects.Count; i++)
        {
            long previousEnd = objects[i - 1] switch
            {
                SliderIr s => s.TimeUs + (long)Math.Round(s.SpanCount * s.ExpectedDistancePx!.Value / (options.SliderMultiplier * 100 * s.SvMultiplier) * analysis.Timing.BeatLengthUs),
                SpinnerIr sp => sp.EndTimeUs,
                _ => objects[i - 1].TimeUs,
            };

            if (objects[i].TimeUs - previousEnd < 5_000_000)
                continue;

            long start = previousEnd + 200_000;
            long end = objects[i].TimeUs - (long)Math.Round(preemptMs * 1000);

            if (end - start >= 650_000)
                breaks.Add(new BreakPeriod(start, end));
        }

        return breaks.ToArray();
    }
}
