namespace OsuAi.Generation;

public enum EventKind
{
    Attack,

    /// <summary>A slider: clicked on its head, its tail lands passively on a weaker sound or a held note.</summary>
    Sustain,
}

/// <summary>
/// One sound the map will represent. Positions are in 1/4-beat grid units of the analysis timing;
/// there are no coordinates here (blueprint 06: MappingPlan).
/// </summary>
public sealed record PlannedEvent
{
    public required int GridIndex { get; init; }
    public required long TimeUs { get; init; }
    public required EventKind Kind { get; init; }
    public int DurationGrid { get; init; }

    /// <summary>Strength of the clicked sound relative to its bar, 0..1. Drives spacing emphasis.</summary>
    public required double Importance { get; init; }

    /// <summary>Distance from the previous object's end in circle radii, when a model learned it.</summary>
    public double? DistanceRadii { get; init; }

    public required int Intensity { get; init; }

    /// <summary>Bars that share a template share rhythm; placement reuses their pattern too.</summary>
    public required int TemplateId { get; init; }

    public int EndGridIndex => GridIndex + DurationGrid;
    public bool IsDownbeat => GridIndex % RhythmSelection.GridPerBar == 0;
    public bool IsBeat => GridIndex % RhythmSelection.GridPerBeat == 0;
}

public sealed record MappingPlan(IReadOnlyList<PlannedEvent> Events, IReadOnlyDictionary<int, int> PhraseIntensityByBar);

/// <summary>
/// The kind of difficulty the plan is for. It decides how much 1/4 rhythm is allowed, since human
/// maps differ there in kind, not just in amount (tools/map-stats/stream_stats.py, training split):
/// below 4 stars only stacked doubles and triples; 4-5.5 stars short stacked bursts (2-5 notes);
/// above that long, overlapping, curved streams.
/// </summary>
public enum DifficultyTier
{
    Hard,
    Insane,
    Expert,
}

/// <summary>
/// B1 rhythm selection, following common mapping practice:
/// <list type="bullet">
/// <item>the loudest sounds are clickable, quieter ones are slider ends or left out;</item>
/// <item>held sounds become sliders;</item>
/// <item>density follows the intensity of the phrase;</item>
/// <item>bars that sound alike get the same rhythm (consistency);</item>
/// <item>runs of 1/2 circles are kept short except at peaks;</item>
/// <item>1/4 notes (bursts, streams) only where the audio has an attack on that 1/4, and only as
/// much as the difficulty tier allows.</item>
/// </list>
/// </summary>
public static class RhythmSelection
{
    public const int GridPerBeat = 4;
    public const int GridPerBar = 16;
    private const int slots_per_bar = 8;
    private const int bars_per_phrase = 4;

    // Calibrated against human maps of the training split (tools/map-stats, 2026-10-02): at 4-6.5 stars
    // mappers keep a continuous 1/2 rhythm (91% of gaps) with 1/2 circle runs of about 10.

    /// <summary>Share of the eight half-beat slots of a bar that are clicked, by intensity.</summary>
    private static readonly double[] click_share = [0.4, 0.55, 0.75, 0.9];

    /// <summary>Longest run of consecutive 1/2 clicks, by intensity.</summary>
    private static readonly int[] max_half_run = [3, 5, 7, 10];

    private const double similar_bar = 0.93;

    public static DifficultyTier TierFor(double stars) => stars < 4 ? DifficultyTier.Hard : stars < 5.5 ? DifficultyTier.Insane : DifficultyTier.Expert;

    public static MappingPlan Plan(SongAnalysis analysis, DifficultyTier tier = DifficultyTier.Insane)
    {
        var phrase = PhraseIntensity(analysis);
        var byIndex = analysis.Grid.ToDictionary(p => p.Index);
        int lastBar = Bar(analysis.Grid[^1].Index);

        var templates = new List<(double[] Signature, int Intensity, bool[] Clicks)>();
        var clicks = new List<(int Slot, double Strength, int Intensity, int Template)>();

        for (int bar = 0; bar <= lastBar; bar++)
        {
            int intensity = phrase.GetValueOrDefault(bar, 0);
            var slots = Enumerable.Range(0, slots_per_bar).Select(s => byIndex.GetValueOrDefault(bar * GridPerBar + s * 2)).ToArray();

            if (slots.All(p => p is null || p.Energy < 0.05))
                continue;

            double[] signature = slots.Select(p => p is null ? 0 : Strength(p)).ToArray();
            int template = templates.FindIndex(t => t.Intensity == intensity && Cosine(t.Signature, signature) >= similar_bar);

            if (template < 0)
            {
                templates.Add((signature, intensity, ChooseClicks(signature, slots, intensity)));
                template = templates.Count - 1;
            }

            var chosen = templates[template].Clicks;
            double max = Math.Max(signature.Max(), 1e-6);

            for (int s = 0; s < slots_per_bar; s++)
            {
                if (chosen[s] && slots[s] is { Energy: >= 0.05 })
                    clicks.Add((bar * slots_per_bar + s, signature[s] / max, intensity, template));
            }
        }

        LimitHalfRuns(clicks);
        var beatSliders = AbsorbWeakClicksIntoSliders(clicks, byIndex);
        var events = BuildEvents(clicks, byIndex, beatSliders);
        return new MappingPlan(AddQuarterNotes(events, byIndex, tier), phrase);
    }

    /// <summary>
    /// B2: clicks where the learned rhythm model (ml/rhythm) gives a probability above its threshold,
    /// at any 1/4 position, so bursts and streams appear where human mappers put them. Only the
    /// difficulty tier's cap on 1/4 runs is enforced on top; the B1 density and run heuristics are
    /// not, because the model learned density from human maps at the target stars. Turning clicks into
    /// circles and sliders follows the same rules as B1.
    /// </summary>
    public static MappingPlan PlanFromModel(SongAnalysis analysis, DifficultyTier tier, RhythmPrediction prediction)
    {
        var phrase = PhraseIntensity(analysis);
        var byIndex = analysis.Grid.ToDictionary(p => p.Index);
        var templateOfBar = TemplateIds(analysis, phrase, byIndex);

        var clicks = prediction.Probability
                               .Where(kv => kv.Value >= prediction.Threshold && byIndex.TryGetValue(kv.Key, out var p) && p.Energy >= 0.05)
                               .Select(kv => kv.Key)
                               .Order()
                               .ToList();

        CapQuarterRuns(clicks, prediction, tier, phrase);

        var events = new List<PlannedEvent>(clicks.Count);

        for (int i = 0; i < clicks.Count; i++)
        {
            int grid = clicks[i];
            int gap = i + 1 < clicks.Count ? clicks[i + 1] - grid : 2 * GridPerBeat;
            int bar = Bar(grid);
            int intensity = phrase.GetValueOrDefault(bar, 0);
            int duration = 0;

            // Same rules as B1: a beat of room makes a 1/2 slider, a beat and a half or a held sound
            // a one-beat slider; anything closer stays a circle.
            if (prediction.Notes is { } notes)
            {
                var note = notes[grid];
                // Leave a free grid tick before the next head and stay inside the analysed audio.
                int available = Math.Min(gap - 1, analysis.Grid[^1].Index - grid);
                duration = note.SliderProbability >= 0.5 ? Math.Max(0, Math.Min(note.DurationGrid, available)) : 0;
            }
            else if (gap >= 6)
            {
                double held = Enumerable.Range(1, 3).Average(k => byIndex.GetValueOrDefault(grid + k)?.Sustain ?? 0);
                duration = held > 0.2 || gap == 6 ? 4 : 2;
            }
            else if (gap >= 4)
            {
                duration = 2;
            }

            events.Add(new PlannedEvent
            {
                GridIndex = grid,
                TimeUs = byIndex[grid].TimeUs,
                Kind = duration > 0 ? EventKind.Sustain : EventKind.Attack,
                DurationGrid = duration,
                Importance = prediction.Notes?.GetValueOrDefault(grid)?.Importance ?? Math.Clamp(prediction.Probability[grid], 0, 1),
                DistanceRadii = prediction.Notes?.GetValueOrDefault(grid)?.DistanceRadii,
                Intensity = intensity,
                TemplateId = templateOfBar.GetValueOrDefault(bar, 0),
            });
        }

        // The model learned mostly from 4-5.5 star maps (few Expert maps in the dataset) and puts about
        // half the 1/4 notes human Expert maps have. For Expert, the audio-driven fill from B1 completes
        // streams where every 1/4 position has an attack, under the same tier caps.
        if (tier == DifficultyTier.Expert && prediction.Notes is null)
            events = AddQuarterNotes(events, byIndex, tier);

        // Model runs and fills can join into one chain; the tier's cap applies to the result.
        CapEventRuns(events, tier, phrase);

        return new MappingPlan(events, phrase);
    }

    /// <summary>
    /// Final cap on chains of consecutive 1/4 circles, whatever produced them: while a chain is longer
    /// than its tier allows, the least important note on a true 1/4 position is removed.
    /// </summary>
    private static void CapEventRuns(List<PlannedEvent> events, DifficultyTier tier, Dictionary<int, int> phrase)
    {
        bool changed = true;

        while (changed)
        {
            changed = false;
            int start = 0;

            for (int i = 1; i <= events.Count; i++)
            {
                bool continues = i < events.Count
                                 && events[i - 1].Kind == EventKind.Attack
                                 && events[i].GridIndex - events[i - 1].GridIndex == 1;

                if (continues)
                    continue;

                int length = i - start;
                int max = Math.Max(1, MaxQuarterRun(tier, phrase.GetValueOrDefault(Bar(events[start].GridIndex), 0)));

                if (length > max)
                {
                    int drop = Enumerable.Range(start + 1, length - 2)
                                         .Where(k => events[k].GridIndex % 2 != 0)
                                         .DefaultIfEmpty(start + length / 2)
                                         .MinBy(k => events[k].Importance);
                    events.RemoveAt(drop);
                    changed = true;
                    break;
                }

                start = i;
            }
        }
    }

    /// <summary>Shortens chains of consecutive 1/4 clicks to the tier's limit, dropping the least likely 1/4 notes.</summary>
    private static void CapQuarterRuns(List<int> clicks, RhythmPrediction prediction, DifficultyTier tier, Dictionary<int, int> phrase)
    {
        bool changed = true;

        while (changed)
        {
            changed = false;
            int start = 0;

            for (int i = 1; i <= clicks.Count; i++)
            {
                if (i < clicks.Count && clicks[i] - clicks[i - 1] == 1)
                    continue;

                int length = i - start;
                int max = Math.Max(1, MaxQuarterRun(tier, phrase.GetValueOrDefault(Bar(clicks[start]), 0)));

                if (length > max)
                {
                    // Remove the least likely note on an odd (true 1/4) position inside the chain.
                    var drop = Enumerable.Range(start, length)
                                         .Where(k => clicks[k] % 2 != 0)
                                         .DefaultIfEmpty(start + length - 1)
                                         .MinBy(k => prediction.Probability.GetValueOrDefault(clicks[k]));
                    clicks.RemoveAt(drop);
                    changed = true;
                    break;
                }

                start = i;
            }
        }
    }

    /// <summary>Bars whose half-beat onsets look alike (cosine ≥ 0.93, same intensity) share a template id.</summary>
    private static Dictionary<int, int> TemplateIds(SongAnalysis analysis, Dictionary<int, int> phrase, Dictionary<int, GridPoint> byIndex)
    {
        var templates = new List<(double[] Signature, int Intensity)>();
        var result = new Dictionary<int, int>();
        int lastBar = Bar(analysis.Grid[^1].Index);

        for (int bar = 0; bar <= lastBar; bar++)
        {
            int intensity = phrase.GetValueOrDefault(bar, 0);
            double[] signature = Enumerable.Range(0, slots_per_bar)
                                           .Select(s => byIndex.GetValueOrDefault(bar * GridPerBar + s * 2) is { } p ? Strength(p) : 0)
                                           .ToArray();
            int template = templates.FindIndex(t => t.Intensity == intensity && Cosine(t.Signature, signature) >= similar_bar);

            if (template < 0)
            {
                templates.Add((signature, intensity));
                template = templates.Count - 1;
            }

            result[bar] = template;
        }

        return result;
    }

    public static int Bar(int gridIndex) => (int)Math.Floor(gridIndex / (double)GridPerBar);

    private static double Strength(GridPoint p) => 0.55 * p.Onset + 0.3 * p.Low + 0.15 * p.High;

    /// <summary>The strongest slots of the bar, with a metrical bonus so beats win ties against offbeats.</summary>
    private static bool[] ChooseClicks(double[] strength, GridPoint?[] slots, int intensity)
    {
        int count = Math.Max(1, (int)Math.Round(click_share[intensity] * slots_per_bar));
        double max = Math.Max(strength.Max(), 1e-6);

        var ranked = Enumerable.Range(0, slots_per_bar)
                               .Where(s => slots[s] is { Energy: >= 0.05 } && strength[s] / max > 0.2)
                               .OrderByDescending(s => strength[s] / max + Metric(s))
                               .Take(count)
                               .ToHashSet();

        return Enumerable.Range(0, slots_per_bar).Select(ranked.Contains).ToArray();
    }

    private static double Metric(int slot) => slot switch
    {
        0 => 0.3,
        4 => 0.2,
        2 or 6 => 0.12,
        _ => 0,
    };

    /// <summary>Breaks long 1/2 runs by dropping the weakest click, which becomes a slider tail instead.</summary>
    private static void LimitHalfRuns(List<(int Slot, double Strength, int Intensity, int Template)> clicks)
    {
        var kept = new List<(int Slot, double Strength, int Intensity, int Template)>(clicks.Count);
        int gaps = 0; // consecutive 1/2 gaps ending at the last kept click

        foreach (var c in clicks)
        {
            bool half = kept.Count > 0 && c.Slot - kept[^1].Slot == 1;
            gaps = half ? gaps + 1 : 0;

            if (gaps > max_half_run[c.Intensity])
            {
                // Prefer keeping the click on the beat: drop an offbeat, either this one or the previous.
                if (c.Slot % 2 == 1)
                {
                    gaps = 0;
                    continue;
                }

                if (kept[^1].Slot % 2 == 1)
                    kept.RemoveAt(kept.Count - 1);

                gaps = 0;
            }

            kept.Add(c);
        }

        clicks.Clear();
        clicks.AddRange(kept);
    }

    /// <summary>
    /// Makes room for one-beat sliders the way mappers do: a click one beat after a stronger click
    /// is dropped when the music holds or when its sound is clearly weaker, so it becomes the tail of a
    /// one-beat slider instead of a click. Humans: about a quarter of sliders are one beat long.
    /// Peaks are left alone; they keep their circles.
    /// </summary>
    /// <returns>Slots whose click must become a one-beat slider, since its tail now carries a sound.</returns>
    private static HashSet<int> AbsorbWeakClicksIntoSliders(List<(int Slot, double Strength, int Intensity, int Template)> clicks, Dictionary<int, GridPoint> byIndex)
    {
        var beatSliders = new HashSet<int>();

        for (int i = 0; i + 2 < clicks.Count; i++)
        {
            var (c, n, m) = (clicks[i], clicks[i + 1], clicks[i + 2]);

            if (c.Intensity >= 3 || n.Slot - c.Slot != 2 || m.Slot - n.Slot < 1 || n.Slot % 2 == 0 && n.Slot % 8 == 0)
                continue;

            double held = Enumerable.Range(1, 3).Average(k => byIndex.GetValueOrDefault(c.Slot * 2 + k)?.Sustain ?? 0);
            bool weaker = n.Strength < c.Strength * 0.8;

            if (held > 0.2 || (weaker && Variation(c.Slot * 31, 0.2)))
            {
                clicks.RemoveAt(i + 1);
                beatSliders.Add(c.Slot);
            }
        }

        return beatSliders;
    }

    /// <summary>
    /// Each click becomes a circle or a slider. A slider when the next click is at least a beat
    /// away and either the sound is held or a weaker sound sits half a beat later for its tail;
    /// it ends half a beat before the next click.
    /// </summary>
    private static List<PlannedEvent> BuildEvents(List<(int Slot, double Strength, int Intensity, int Template)> clicks, Dictionary<int, GridPoint> byIndex, HashSet<int> beatSliders)
    {
        var events = new List<PlannedEvent>(clicks.Count);

        for (int i = 0; i < clicks.Count; i++)
        {
            var c = clicks[i];
            int grid = c.Slot * 2;
            int gapSlots = i + 1 < clicks.Count ? clicks[i + 1].Slot - c.Slot : 4;
            var point = byIndex[grid];
            int duration = 0;

            // Human maps of the training split keep the cursor busy: 91% of gaps between one object's
            // end and the next click are half a beat, 3% are a full beat. So a click followed by room
            // becomes a slider that ends half a beat before the next click: one beat long when there is
            // a beat and a half (or the music holds), half a beat when there is one beat. Circles stay
            // for consecutive half-beat clicks, which is where the jumps are.
            if (gapSlots >= 3)
            {
                double held = Enumerable.Range(1, 3).Average(k => byIndex.GetValueOrDefault(grid + k)?.Sustain ?? 0);
                duration = held > 0.2 || beatSliders.Contains(c.Slot) || gapSlots == 3 ? 4 : 2;
            }
            else if (gapSlots == 2)
            {
                duration = 2;
            }

            events.Add(new PlannedEvent
            {
                GridIndex = grid,
                TimeUs = point.TimeUs,
                Kind = duration > 0 ? EventKind.Sustain : EventKind.Attack,
                DurationGrid = duration,
                Importance = Math.Clamp(c.Strength, 0, 1),
                Intensity = c.Intensity,
                TemplateId = c.Template,
            });
        }

        return events;
    }

    /// <summary>Longest 1/4 run (notes, including its ends) by tier and intensity.</summary>
    private static int MaxQuarterRun(DifficultyTier tier, int intensity) => (tier, intensity) switch
    {
        (DifficultyTier.Hard, >= 2) => 3,
        (DifficultyTier.Hard, _) => 0,
        (DifficultyTier.Insane, >= 2) => 5,
        (DifficultyTier.Insane, 1) => 3,
        (DifficultyTier.Insane, _) => 0,
        (DifficultyTier.Expert, >= 3) => 24,
        (DifficultyTier.Expert, 2) => 16,
        (DifficultyTier.Expert, 1) => 5,
        _ => 0,
    };

    /// <summary>How loud a 1/4 position must be, relative to the loudest sound of its bar.</summary>
    private static double QuarterThreshold(DifficultyTier tier) => tier switch
    {
        DifficultyTier.Hard => 0.6,
        DifficultyTier.Insane => 0.5,
        // Expert streams ride continuous hi-hats and fills that are quieter than the beat.
        _ => 0.32,
    };

    /// <summary>
    /// Fills the 1/4 positions between a circle and the next click when every one of them carries an
    /// attack (drum fills, fast hi-hats, quick syllables). All or nothing, so a fill never invents a
    /// rhythm the music does not play; runs are capped by tier and intensity.
    /// </summary>
    private static List<PlannedEvent> AddQuarterNotes(List<PlannedEvent> events, Dictionary<int, GridPoint> byIndex, DifficultyTier tier)
    {
        var result = new List<PlannedEvent>(events.Count);
        double threshold = QuarterThreshold(tier);
        int run = 1; // notes in the 1/4 chain that ends at the current event

        double BarMax(int grid)
        {
            int start = Bar(grid) * GridPerBar;
            return Math.Max(1e-6, Enumerable.Range(start, GridPerBar).Max(i => byIndex.TryGetValue(i, out var p) ? Strength(p) : 0));
        }

        for (int i = 0; i < events.Count; i++)
        {
            var a = events[i];
            var b = i + 1 < events.Count ? events[i + 1] : null;
            int gap = b is null ? 0 : b.GridIndex - a.GridIndex;

            // In Expert a half-beat slider spanning a beat-long gap can give way to a stream when the
            // audio plays every 1/4 there; otherwise sliders end a chain.
            bool streamable = a.Kind == EventKind.Attack || (tier == DifficultyTier.Expert && a.DurationGrid == 2 && gap == 4);

            if (b is null || !streamable)
            {
                result.Add(a);
                run = 1;
                continue;
            }

            // Already a 1/4 apart (the rhythm model can place consecutive 1/4 clicks itself): the chain
            // grows without a fill, and must be counted, or a later fill would extend it past the cap.
            if (gap == 1)
            {
                result.Add(a);
                run++;
                continue;
            }

            result.Add(a);
            int max = MaxQuarterRun(tier, a.Intensity);
            double barMax = BarMax(a.GridIndex);
            var fills = Enumerable.Range(a.GridIndex + 1, Math.Max(0, gap - 1)).ToArray();

            // The fill joins a and b into one chain of run + fills + 1 notes (b may be a slider head:
            // a burst into a slider is common; the slider then ends the chain).
            bool fits = gap is 2 or 4 && run + fills.Length + 1 <= max;

            // Momentum: human streams carry on through a slightly weaker 1/4 once they are under way,
            // so an established stream (6+ notes) continues on half the evidence.
            double needed = run >= 6 ? threshold * 0.5 : threshold;
            bool allStrong = fits && fills.All(g => byIndex.TryGetValue(g, out var p) && p.Energy >= 0.05 && Strength(p) / barMax >= needed);

            if (!allStrong)
            {
                run = 1;
                continue;
            }

            if (a.Kind == EventKind.Sustain)
                result[^1] = a with { Kind = EventKind.Attack, DurationGrid = 0 };

            foreach (int g in fills)
            {
                result.Add(new PlannedEvent
                {
                    GridIndex = g,
                    TimeUs = byIndex[g].TimeUs,
                    Kind = EventKind.Attack,
                    Importance = Math.Clamp(Strength(byIndex[g]) / barMax, 0, 1),
                    Intensity = a.Intensity,
                    TemplateId = a.TemplateId,
                });
            }

            // b joins the chain even when it is a slider: in Expert it may be turned into a circle on the
            // next step to continue the stream, and the cap must count through that join. If it stays a
            // slider, the next step cannot extend the chain and resets the count.
            run += fills.Length + 1;
        }

        return result;
    }

    /// <summary>A deterministic yes with the given probability, keyed by position, so repeated runs agree.</summary>
    internal static bool Variation(int key, double probability)
    {
        // A fixed integer mix: HashCode.Combine is randomised per process and would break determinism.
        uint h = unchecked((uint)key * 2654435761u);
        h ^= h >> 16;
        h = unchecked(h * 0x45d9f3bu);
        h ^= h >> 16;
        return h % 1000 < probability * 1000;
    }

    private static double Cosine(double[] a, double[] b)
    {
        double dot = 0, na = 0, nb = 0;

        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>Section intensities smoothed over four-bar phrases so density does not flicker bar to bar.</summary>
    private static Dictionary<int, int> PhraseIntensity(SongAnalysis analysis)
    {
        var perBar = new Dictionary<int, int>();

        foreach (var s in analysis.Sections)
        {
            for (int b = s.FirstBar; b < s.FirstBar + s.BarCount; b++)
                perBar[b] = s.Intensity;
        }

        var result = new Dictionary<int, int>();

        foreach (var group in perBar.GroupBy(kv => (int)Math.Floor(kv.Key / (double)bars_per_phrase)))
        {
            int level = (int)Math.Round(group.Average(kv => kv.Value), MidpointRounding.AwayFromZero);

            foreach (var kv in group)
                result[kv.Key] = level;
        }

        return result;
    }
}
