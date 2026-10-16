using OsuAi.BeatmapCore;
using OsuAi.Generation;

namespace OsuAi.Application;

public enum IssueSeverity
{
    /// <summary>Worth a look; often fine.</summary>
    Notice,

    /// <summary>Likely to feel wrong in play.</summary>
    Warning,

    /// <summary>Breaks the map: unplayable or plainly wrong.</summary>
    Problem,
}

/// <summary>One thing found in a generated map, at a time in the song, in Portuguese for the user.</summary>
public sealed record MapIssue(IssueSeverity Severity, long TimeMs, long? EndMs, string Message);

/// <summary>
/// Checks a generated map for what went wrong in maps the user played: stretches left empty while
/// the music plays, an ending before the song ends, objects piled on one instant, bursts of density
/// and jumps far outside the map's own range, and stars far from the target. Found before the user
/// opens the map in osu!, with the time of each.
/// </summary>
public static class MapReport
{
    private const int MaxPerKind = 5;

    public static IReadOnlyList<MapIssue> Check(BeatmapIr map, SongAnalysis analysis, double targetStars, double actualStars, (long StartMs, long EndMs)? span = null)
    {
        var issues = new List<MapIssue>();
        var objects = map.Objects.OrderBy(o => o.TimeUs).ToArray();
        long songMs = analysis.Audio.DurationUs / 1000;

        // Stars first: not tied to a moment of the song.
        var stars = new List<MapIssue>();
        if (Math.Abs(actualStars - targetStars) > 0.5)
            stars.Add(new MapIssue(Math.Abs(actualStars - targetStars) > 1 ? IssueSeverity.Warning : IssueSeverity.Notice, 0, null,
                $"{actualStars:0.00}★ para um alvo de {targetStars:0.0#}★."));

        if (objects.Length == 0)
        {
            stars.Add(new MapIssue(IssueSeverity.Problem, 0, null, "O mapa não tem objetos."));
            return stars;
        }

        double[] times = objects.Select(o => o.TimeUs / 1000.0).ToArray();

        var outside = objects.Where(o => o.TimeUs < 0 || o.TimeUs / 1000 > songMs).ToArray();
        if (outside.Length > 0)
            issues.Add(new MapIssue(IssueSeverity.Problem, Math.Max(0, outside[0].TimeUs / 1000), null, $"{outside.Length} objeto(s) fora da duração da música."));

        // Several objects on the same instant: in osu!standard that is never playable.
        var piles = new List<(long Time, int Count)>();
        for (int i = 0; i < times.Length;)
        {
            int j = i;
            while (j + 1 < times.Length && times[j + 1] - times[i] < 5)
                j++;
            if (j > i)
                piles.Add(((long)times[i], j - i + 1));
            i = j + 1;
        }
        foreach (var (time, count) in piles.Take(MaxPerKind))
            issues.Add(new MapIssue(IssueSeverity.Problem, time, null, $"{count} objetos no mesmo instante."));
        if (piles.Count > MaxPerKind)
            issues.Add(new MapIssue(IssueSeverity.Problem, piles[MaxPerKind].Time, null, $"Mais {piles.Count - MaxPerKind} lugares com objetos no mesmo instante."));

        foreach (var (from, to) in EmptyStretches(times, analysis, 3).Take(MaxPerKind))
            issues.Add(new MapIssue(IssueSeverity.Warning, from, to, $"Trecho sem notas de {(to - from) / 1000.0:0.#} s com a música tocando."));

        if (span is null)
        {
            if (Loud(analysis, 0, times[0]) && times[0] > 8000)
                issues.Add(new MapIssue(IssueSeverity.Notice, 0, (long)times[0], $"As notas só começam em {Clock(times[0])}, com a música já tocando."));
            double last = objects.Max(EndMs);
            if (songMs - last > 8000 && Loud(analysis, last, songMs))
                issues.Add(new MapIssue(IssueSeverity.Warning, (long)last, songMs, $"O mapa termina em {Clock(last)}, {(songMs - last) / 1000:0} s antes da música acabar."));
        }

        issues.AddRange(DensitySpikes(times));
        issues.AddRange(OutlierJumps(objects));
        return [.. stars, .. issues.OrderBy(i => i.TimeMs)];
    }

    /// <summary>Gaps of at least <paramref name="minSeconds"/> between objects while the music plays (mean loudness at least half the median).</summary>
    public static IReadOnlyList<(long StartMs, long EndMs)> EmptyStretches(IReadOnlyList<double> sortedTimesMs, SongAnalysis analysis, double minSeconds)
    {
        var stretches = new List<(long, long)>();
        for (int i = 1; i < sortedTimesMs.Count; i++)
        {
            if (sortedTimesMs[i] - sortedTimesMs[i - 1] >= minSeconds * 1000 && Loud(analysis, sortedTimesMs[i - 1], sortedTimesMs[i]))
                stretches.Add(((long)sortedTimesMs[i - 1], (long)sortedTimesMs[i]));
        }
        return stretches;
    }

    private static bool Loud(SongAnalysis analysis, double fromMs, double toMs)
    {
        var grid = analysis.Grid.Where(p => p.Index >= 0).ToArray();
        if (grid.Length == 0)
            return false;
        double median = grid.Select(p => p.Energy).Order().ElementAt(grid.Length / 2);
        var inside = grid.Where(p => p.TimeUs / 1000.0 > fromMs && p.TimeUs / 1000.0 < toMs).ToArray();
        return inside.Length > 0 && inside.Average(p => p.Energy) >= 0.5 * median;
    }

    /// <summary>One-second windows (every half second) with well over the map's usual density and at least 10 objects.</summary>
    private static IEnumerable<MapIssue> DensitySpikes(double[] times)
    {
        double first = times[0], last = times[^1];
        var windows = new List<(double Start, int Count)>();
        int lo = 0, hi = 0;
        for (double start = first; start <= last; start += 500)
        {
            while (lo < times.Length && times[lo] < start)
                lo++;
            while (hi < times.Length && times[hi] < start + 1000)
                hi++;
            windows.Add((start, hi - lo));
        }
        var busy = windows.Where(w => w.Count > 0).Select(w => w.Count).Order().ToArray();
        if (busy.Length == 0)
            yield break;
        double median = busy[busy.Length / 2];
        int reported = 0;
        double lastEnd = double.NegativeInfinity;
        foreach (var (start, count) in windows)
        {
            if (count < 10 || count < 2.5 * median || start < lastEnd)
                continue;
            lastEnd = start + 3000;
            if (++reported > MaxPerKind)
                yield break;
            yield return new MapIssue(IssueSeverity.Warning, (long)start, (long)start + 1000, $"{count} objetos em 1 s, {count / median:0.#}× a densidade normal do mapa.");
        }
    }

    /// <summary>
    /// Jumps much faster than the map's own fast ones: speed (distance over time from the end of one
    /// object to the next) above 1.6 times its 95th percentile and above 2.5 px/ms.
    /// </summary>
    private static IEnumerable<MapIssue> OutlierJumps(HitObjectIr[] objects)
    {
        var moves = new List<(long Time, double Distance, double Speed)>();
        for (int i = 1; i < objects.Length; i++)
        {
            var (a, b) = (objects[i - 1], objects[i]);
            if (a is SpinnerIr || b is SpinnerIr)
                continue;
            double gap = b.TimeUs / 1000.0 - EndMs(a);
            if (gap < 20)
                continue;
            var from = a is SliderIr { Computed: { } c } ? c.EndPosition : a.Position;
            double distance = Math.Sqrt(Math.Pow(b.Position.X - from.X, 2) + Math.Pow(b.Position.Y - from.Y, 2));
            moves.Add((b.TimeUs / 1000, distance, distance / gap));
        }
        if (moves.Count < 20)
            yield break;
        double p95 = moves.Select(m => m.Speed).Order().ElementAt((int)(moves.Count * 0.95));
        int reported = 0;
        foreach (var move in moves.Where(m => m.Speed > Math.Max(2.5, 1.6 * p95)))
        {
            if (++reported > MaxPerKind)
                yield break;
            yield return new MapIssue(IssueSeverity.Notice, move.Time, null, $"Salto de {move.Distance:0} px muito acima dos outros do mapa ({move.Speed / p95:0.#}× os mais rápidos).");
        }
    }

    private static double EndMs(HitObjectIr o) => o switch
    {
        SpinnerIr s => s.EndTimeUs / 1000.0,
        SliderIr { Computed: { } c } => (o.TimeUs + c.DurationUs) / 1000.0,
        _ => o.TimeUs / 1000.0,
    };

    public static string Clock(double ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"m\:ss");
}

/// <summary>
/// Approach rate, overall difficulty and HP by star rating, as ranked maps of each tier commonly
/// set them, for when the user leaves them automatic. CS is left to the engine.
/// </summary>
public static class DifficultyTiers
{
    public static (double Ar, double Od, double Hp) For(double stars) => stars switch
    {
        < 2.0 => (5, 4, 3.5),
        < 2.7 => (6.5, 5, 4),
        < 4.0 => (8, 7, 5),
        < 5.3 => (9, 8, 5),
        < 6.5 => (9.4, 8.8, 5.5),
        _ => (9.7, 9.2, 6),
    };

    /// <summary>A name for a difficulty of this many stars, as osu! colours them.</summary>
    public static string Name(double stars) => stars switch
    {
        < 2.0 => "Easy",
        < 2.7 => "Normal",
        < 4.0 => "Hard",
        < 5.3 => "Insane",
        < 6.5 => "Expert",
        _ => "Expert+",
    };
}

/// <summary>Where to make a preview: the loudest stretch of the song (normally the chorus or the drop).</summary>
public static class PreviewSpan
{
    public static (long StartMs, long EndMs) Loudest(SongAnalysis analysis, double seconds = 30)
    {
        var grid = analysis.Grid.Where(p => p.Index >= 0).OrderBy(p => p.TimeUs).ToArray();
        long songMs = analysis.Audio.DurationUs / 1000;
        long length = (long)(seconds * 1000);
        if (songMs <= length || grid.Length == 0)
            return (0, songMs);

        double best = double.NegativeInfinity;
        long bestStart = 0;
        int lo = 0;
        double sum = 0;
        for (int hi = 0; hi < grid.Length; hi++)
        {
            sum += grid[hi].Energy;
            while (grid[hi].TimeUs / 1000 - grid[lo].TimeUs / 1000 > length)
                sum -= grid[lo++].Energy;
            long start = grid[lo].TimeUs / 1000;
            if (start + length <= songMs && sum > best)
            {
                best = sum;
                bestStart = start;
            }
        }
        return (bestStart, bestStart + length);
    }
}

/// <summary>
/// Mapperatorinator's star request is approximate (5 asked, 3.98 given on one clip). The next request
/// is corrected by how far the last ones on this project landed, previews included.
/// </summary>
public static class StarCalibration
{
    public static double Request(double target, IReadOnlyList<OsuAi.ProjectStore.StarObservation> observations)
    {
        var recent = observations.TakeLast(3).ToArray();
        if (recent.Length == 0)
            return target;
        double offset = recent.Average(o => o.Requested - o.Actual);
        return Math.Round(Math.Clamp(target + offset, Math.Max(0.5, target - 2), Math.Min(12, target + 2)), 2);
    }
}
