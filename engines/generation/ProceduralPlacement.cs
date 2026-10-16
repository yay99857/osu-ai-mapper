using OsuAi.BeatmapCore;

namespace OsuAi.Generation;

public sealed record PlacementSettings
{
    public required double CircleSize { get; init; }
    public required double SliderMultiplier { get; init; }

    /// <summary>
    /// Distance of a 1/2 circle jump as a multiple of the half-beat slider distance. The knob the
    /// star search turns; flow distances follow it more gently.
    /// </summary>
    public required double SpacingFactor { get; init; }

    public required int Seed { get; init; }

    public DifficultyTier Tier { get; init; } = DifficultyTier.Insane;
}

/// <param name="SliderLengthPx">Length of one span; the slider travels it <paramref name="SpanCount"/> times.</param>
/// <param name="Velocity">Slider velocity multiplier; below 1 for slow sliders.</param>
public sealed record PlacedObject(PlannedEvent Event, Vector2D Position, SliderPathIr? Path, double SliderLengthPx, int SpanCount, Vector2D EndPosition, bool NewCombo, string Pattern, double Velocity = 1);

/// <summary>Movement shapes from common mapping practice (osu! wiki: Flow, Jumps).</summary>
public enum Shape
{
    /// <summary>Smooth circular movement, turning a little each object, direction switched every combo.</summary>
    Flow,

    /// <summary>1/2 jumps turning 120°: a triangle that the next jumps trace again.</summary>
    Triangle,

    /// <summary>1/2 jumps turning 90°.</summary>
    Square,

    /// <summary>1/2 jumps going back and forth with a slight tilt; the easiest jumps to land.</summary>
    BackAndForth,

    /// <summary>1/2 jumps turning 144°: a five-pointed star.</summary>
    Star,

    /// <summary>Alternating ±135° turns: a zigzag that travels across the playfield.</summary>
    Zigzag,

    /// <summary>
    /// "1-2" jumps: back and forth between two spots, each object landing exactly where the one two
    /// before it was (X Z X Z). Human Insane and Expert maps turn 165-180° in 10% of their 1/2 jumps.
    /// </summary>
    Alternate,
}

/// <summary>
/// Places a rhythm plan with recognisable patterns, calibrated against human maps of the training
/// split (tools/map-stats, 2026-10-02). At 4-6.5 stars those mappers turn 120-180° on three moves out
/// of four and keep about the same distance after a 1/2 or a 1 beat gap, so:
/// <list type="bullet">
/// <item>every move of a combo follows one shape (zigzag, triangle, star, back-and-forth); calm
/// sections use gentle circular flow instead;</item>
/// <item>distance is constant for gaps up to one beat (visual spacing), larger on strong downbeats;</item>
/// <item>slider bodies kick sideways from the movement, about 60% curved, some one-beat sliders reverse;</item>
/// <item>bars sharing a rhythm template share their shape;</item>
/// <item>nothing within a beat may overlap, and walls mirror the pattern instead of scattering it.</item>
/// </list>
/// Deterministic for a given seed. A placeholder for learned placement (B3), not a style.
/// </summary>
public static class ProceduralPlacement
{
    private const double width = BeatmapValidator.PlayfieldWidth;
    private const double height = BeatmapValidator.PlayfieldHeight;

    // Weighted towards the turns of human Insane and Expert maps (angle_study.py on 120k 1/2 jumps):
    // 135-165° (stars) 46%, 105-135° (triangles) 26%, 75-105° (squares) 10%, 165-180° (1-2) 10%.
    // Before, squares were never chosen and back-and-forth turned 150°, so nothing went straight back.
    private static readonly Shape[] jump_shapes =
        [Shape.Star, Shape.Triangle, Shape.Zigzag, Shape.Star, Shape.Square, Shape.Triangle, Shape.Alternate, Shape.Star, Shape.Zigzag, Shape.BackAndForth];

    private sealed record Recent(long TimeUs, Vector2D[] Points);

    private static readonly double[] loudness_weight = [0.2, 0.5, 1.0, 1.4];

    /// <summary>
    /// How a burst of up to five 1/4 notes sits, by how long a 1/4 lasts. Measured on 1,197 human
    /// Insane and Expert maps (training split; Sotarks alone in brackets): under 100 ms 65% (80%)
    /// of triplets are stacked on one spot, the rest spread about 1.4 radii end to end; 100-120 ms
    /// 37% (29%) stacked, the spaced ones about 2 radii; from 120 ms on 8% (0%) stacked and spaced
    /// like small jumps, about 3.7 (4.9) radii end to end. Placement used to stack only under
    /// 100 ms and never in Expert, and to space every other burst 0.6 radius apart.
    /// </summary>
    public static (bool Stacked, double StepRadii) BurstLayout(double quarterMs, int seed)
    {
        var (stackShare, step) = quarterMs switch
        {
            < 100 => (0.7, 0.7),
            < 120 => (0.35, 1.0),
            _ => (0.08, 1.8),
        };
        return (RhythmSelection.Variation(seed, stackShare), step);
    }

    /// <summary>
    /// How many times a slider travels its body, and at what velocity. Human Insane and Expert maps
    /// (training split) rarely repeat a slider: up to a beat 82-98% travel once; one to two beats
    /// half go once and most of the rest there and back; beyond two beats 72% travel once as a long,
    /// slow slider. The old rule split every long slider into spans of at most 220 px, so a
    /// four-beat slider went back and forth four times. A body longer than 280 px is slowed down
    /// instead; the velocity never drops below 0.35.
    /// </summary>
    public static (int Spans, double Velocity) SliderTravel(int durationGrid, double totalPx, int seed, double baseVelocity = 1)
    {
        double beats = durationGrid / (double)RhythmSelection.GridPerBeat;
        int spans = beats switch
        {
            <= 0.75 => 1,
            <= 1 => RhythmSelection.Variation(seed, 0.07) ? 2 : 1,
            <= 2 => RhythmSelection.Variation(seed, 0.5) ? 2 : 1,
            _ => RhythmSelection.Variation(seed, 0.15) ? 2 : 1,
        };
        double body = totalPx * baseVelocity / spans;
        double velocity = body > max_slider_body_px ? Math.Max(0.35, max_slider_body_px / (totalPx / spans)) : baseVelocity;
        return (spans, velocity);
    }

    private const double max_slider_body_px = 280;

    private static readonly double[] insane_stream_step = [0.8, 0.88, 0.95, 1.0];
    private static readonly double[] expert_stream_step = [1.0, 1.15, 1.25, 1.3];

    /// <summary>
    /// Distance between consecutive notes of a stream (five or more 1/4 notes), in circle radii, by
    /// tier and phrase intensity 0..3. Human streams (training split, stream_spacing_study.py) widen
    /// with the music: Insane from 0.77 in the quietest fifth of a song to 0.98 in the loudest,
    /// Expert from 0.98 to 1.29, half or more of loud-passage streams spaced over a radius. Streams
    /// were placed 0.6 (Insane) and 0.7 (Expert) apart everywhere, and stacked in Insane under 100 ms.
    /// </summary>
    public static double StreamStep(DifficultyTier tier, int intensity) =>
        (tier == DifficultyTier.Expert ? expert_stream_step : insane_stream_step)[Math.Clamp(intensity, 0, 3)];

    private static readonly double[] section_velocity = [0.85, 1.0, 1.1, 1.2];

    /// <summary>
    /// Slider velocity by phrase intensity 0..3. Human Insane and Expert maps (600 maps, training
    /// split; 99% change velocity within the map) slow sliders down in calm passages and speed them
    /// up in loud ones: median 0.85 in the quietest fifth of a song's bars (51% under 0.9), 1.0 in the
    /// middle, 1.2 in the loudest (58% over 1.1, p90 2.0).
    /// </summary>
    public static double SectionVelocity(int intensity) => section_velocity[Math.Clamp(intensity, 0, section_velocity.Length - 1)];

    /// <summary>The spacing factor's effect on a learned distance, weighted by phrase intensity 0..3.</summary>
    public static double LoudnessScaled(double factor, int intensity) =>
        1 + (factor - 1) * loudness_weight[Math.Clamp(intensity, 0, loudness_weight.Length - 1)];

    public static IReadOnlyList<PlacedObject> Place(MappingPlan plan, PlacementSettings settings, double beatLengthUs)
    {
        double radius = 54.4 - 4.48 * settings.CircleSize;
        double margin = radius;
        double halfBeatPx = settings.SliderMultiplier * 100 / 2;

        var result = new List<PlacedObject>(plan.Events.Count);
        var recent = new List<Recent>();
        var position = new Vector2D(256, 192);
        double heading = -Math.PI / 2;
        int flowSign = 1;
        int previousEnd = int.MinValue;
        int comboLength = 0;
        int comboIndex = -1;
        Shape jumpShape = Shape.Triangle;
        long comboStartUs = 0;
        int streamLength = 0;
        var streamPath = new Queue<Vector2D>();
        int[] streamTotal = QuarterRunLengths(plan.Events);
        bool runStacked = false;

        for (int i = 0; i < plan.Events.Count; i++)
        {
            var e = plan.Events[i];
            var previous = i > 0 ? plan.Events[i - 1] : null;
            int gapGrid = previous is null ? 4 * RhythmSelection.GridPerBeat : e.GridIndex - previousEnd;
            double gapBeats = gapGrid / (double)RhythmSelection.GridPerBeat;
            bool quarter = previous is { Kind: EventKind.Attack } && e.Kind == EventKind.Attack && gapGrid == 1;

            if (!quarter)
                streamLength = 0;
            bool newCombo = previous is null || (!quarter && StartsCombo(e, previous, gapGrid, comboLength));

            if (quarter)
            {
                // Bursts (up to five notes, the run human Insane maps stack; ADR-027) follow
                // BurstLayout, measured on human triplets. Longer
                // streams overlap (about 0.7 radius apart in Expert) and curve, about 30° per note
                // in short ones. A long stream at that curvature closes into a loop every 12 notes and
                // becomes unreadable, so the whole stream bends at most ~160°, centred on the open
                // side of the playfield.
                double quarterMs = beatLengthUs / 4 / 1000;
                int runTotal = Math.Max(1, streamTotal[i]);
                bool burst = runTotal <= 4;
                // Decided once per run, at its first 1/4 note.
                bool runStart = i < 2 || !(plan.Events[i - 2].Kind == EventKind.Attack && previous!.GridIndex - plan.Events[i - 2].EndGridIndex == 1);
                if (runStart)
                    runStacked = burst && BurstLayout(quarterMs, e.GridIndex * 19 + settings.Seed).Stacked;
                bool stack = runStacked;
                var next = position;

                if (!stack)
                {
                    streamLength++;

                    if (streamLength == 1)
                    {
                        int total = runTotal;
                        double step = burst
                            ? radius * BurstLayout(quarterMs, e.GridIndex * 19 + settings.Seed).StepRadii
                            // Twelve notes or more at the human spacing do not fit the playfield without
                            // curling back over themselves, so long streams stay compact.
                            : radius * (total >= 12 ? Math.Min(0.75, StreamStep(settings.Tier, e.Intensity)) : StreamStep(settings.Tier, e.Intensity));
                        // Long streams do not fit as one arc (23 notes at 0.7 radius are ~600 px), so they
                        // snake: two opposite bends. Shorter ones curve one way, sometimes as an S.
                        bool sCurve = total >= 12 || (total >= 8 && RhythmSelection.Variation(e.GridIndex * 11 + settings.Seed, 0.3));
                        streamPath = PlanStream(position, total, step, flowSign, sCurve, margin, radius, recent, e.TimeUs, beatLengthUs);
                    }

                    next = streamPath.Count > 0 ? streamPath.Dequeue() : position;
                    heading = Math.Atan2(next.Y - position.Y, next.X - position.X);
                }

                position = next;
                result.Add(new PlacedObject(e, position, null, 0, 1, position, false, stack ? "Burst" : "Stream"));
                recent.Add(new Recent(e.TimeUs, [position]));
                previousEnd = e.EndGridIndex;
                comboLength++;
                continue;
            }

            if (newCombo)
            {
                comboIndex++;
                comboLength = 0;
                comboStartUs = e.TimeUs;
                flowSign = comboIndex % 2 == 0 ? 1 : -1;
                // The template picks the jump shape, so repeated music repeats its patterns.
                jumpShape = jump_shapes[(e.TemplateId + settings.Seed) % jump_shapes.Length];
            }

            bool calm = e.Intensity <= 1 && RhythmSelection.Variation(comboIndex * 17 + settings.Seed, 0.5);
            bool emphasis = e.IsDownbeat && e.Importance > 0.7 && e.Intensity >= 2;
            var shape = calm ? Shape.Flow : jumpShape;
            string pattern = emphasis ? "Emphasis" : shape.ToString();

            double turn;
            double distance;

            if (previous is null)
            {
                turn = 0;
                distance = 0;
            }
            else
            {
                // A little deterministic jitter so the shapes are not drawn with a ruler.
                double jitter = Radians(RhythmSelection.Variation(e.GridIndex * 3 + settings.Seed, 0.5) ? 15 : -15) * (RhythmSelection.Variation(e.GridIndex * 5, 0.4) ? 1 : 0);
                turn = flowSign * Turn(shape, comboLength) + jitter;
                // A learned distance (Pulso v2) already varies with the music. The spacing factor
                // raises it where the music is loudest and barely in calm passages: harder human
                // maps of a song keep its rhythm and calm parts and widen the chorus jumps (SPYAIR
                // Imagination, Extra 5.4* -> Ambition 6.1*: same density, chorus jumps +20-30%).
                // Otherwise the same visual distance up to a one-beat gap.
                distance = e.DistanceRadii is { } learned
                    ? learned * radius * LoudnessScaled(settings.SpacingFactor, e.Intensity)
                    : settings.SpacingFactor * halfBeatPx * (gapBeats > 1 ? 0.85 : 1.0) * (emphasis ? 1.2 : 1.0) * (calm ? 0.7 : 1.0);
            }

            distance = Math.Max(distance, previous is null ? 0 : radius * 1.6);

            var (candidate, newHeading, mirrored) = previous is null
                ? (position, heading, false)
                : Step(position, heading, turn, distance, margin, radius, recent, e.TimeUs, beatLengthUs, comboStartUs);

            if (mirrored)
                flowSign = -flowSign;

            // 1-2 jumps land exactly on the circle two objects back, when the last two were circles a
            // 1/2 apart in this combo; otherwise the 180° turn above stands.
            if (shape == Shape.Alternate && comboLength >= 2 && gapGrid == 2 && result.Count >= 2
                && result[^1].Path is null && result[^2].Path is null && !result[^1].NewCombo
                && e.GridIndex - result[^1].Event.GridIndex == 2)
            {
                candidate = result[^2].Position;
                newHeading = Math.Atan2(candidate.Y - position.Y, candidate.X - position.X);
            }
            else if (previous is not null)
                candidate = SnapToStack(candidate, recent, e.TimeUs, beatLengthUs, comboStartUs, radius);

            position = candidate;
            heading = newHeading;

            if (e.Kind == EventKind.Sustain)
            {
                double total = halfBeatPx * 2 * e.DurationGrid / RhythmSelection.GridPerBeat;
                var (spans, velocity) = SliderTravel(e.DurationGrid, total, e.GridIndex * 7 + settings.Seed, SectionVelocity(e.Intensity));
                double length = total * velocity / spans;
                bool curved = RhythmSelection.Variation(e.GridIndex * 13 + settings.Seed, 0.6);

                // The body kicks sideways from the movement; the next move keeps turning from the
                // direction the cursor was travelling, so the shape of the combo survives sliders.
                double bodyAngle = heading + flowSign * Radians(calm ? 20 : 75);
                var slider = SliderShape(position, bodyAngle, flowSign, length, curved, margin, radius, recent, e.TimeUs, beatLengthUs, comboStartUs);
                var finish = spans % 2 == 0 ? position : slider.End;

                result.Add(new PlacedObject(e, position, slider.Path, length, spans, finish, newCombo, pattern + "+Slider", velocity));
                recent.Add(new Recent(e.TimeUs, slider.Body));
                position = finish;
                heading = calm ? slider.ExitHeading : heading;
            }
            else
            {
                result.Add(new PlacedObject(e, position, null, 0, 1, position, newCombo, pattern));
                recent.Add(new Recent(e.TimeUs, [position]));
            }

            previousEnd = e.EndGridIndex;
            comboLength++;
        }

        return result;
    }

    /// <summary>
    /// The whole path of a stream, decided before its first note. Short streams curve about 30° per
    /// note like human ones. A single bend never exceeds ~160°, so a stream never closes into a loop;
    /// an S has two such bends in opposite senses. Candidate exit directions are tried from the open
    /// side of the playfield outwards, in both senses; the first path that stays inside and clear of
    /// recent objects wins, otherwise the one with the most clearance.
    /// </summary>
    private static Queue<Vector2D> PlanStream(Vector2D start, int total, double step, int flowSign, bool sCurve, double margin, double radius,
                                              List<Recent> recent, long timeUs, double beatLengthUs)
    {
        double toCentre = Math.Atan2(192 - start.Y, 256 - start.X);
        List<Vector2D>? best = null;
        double bestClearance = double.NegativeInfinity;

        // Shapes to try, as (bends, degrees per bend). Short streams: one arc of about 30° per note.
        // Longer: an S, then single arcs of growing sweep up to an open ring (330°), the compact
        // "circle stream" humans use, then snakes with more bends. The self-overlap check below
        // rejects any shape that would fold onto itself.
        (int Bends, double Sweep)[] shapes = total < 8
            ? [(1, Math.Min(150, 30.0 * total))]
            : sCurve
                ? [(2, 150), (1, 150), (1, 240), (1, 330), (3, 150), (4, 150)]
                : [(1, 150), (2, 150), (1, 240), (1, 330)];

        foreach (var (bends, sweep) in shapes)
        {
            double perBend = total / (double)bends;
            double turn = Radians(sweep / perBend);

            foreach (int offset in new[] { 0, 30, -30, 60, -60, 90, -90, 120, -120, 150, -150, 180 })
            {
                foreach (int sign in new[] { flowSign, -flowSign })
                {
                    var path = new List<Vector2D>(total);
                    // Centre the first bend on the chosen direction.
                    double heading = toCentre + Radians(offset) - sign * turn * perBend / 2;
                    var at = start;
                    int curve = sign;

                    for (int k = 1; k <= total; k++)
                    {
                        if (k > 1 && (int)((k - 1) / perBend) != (int)((k - 2) / perBend))
                            curve = -curve;

                        heading += curve * turn;
                        at = new Vector2D(at.X + step * Math.Cos(heading), at.Y + step * Math.Sin(heading));
                        path.Add(at);
                    }

                    if (!path.All(q => Inside(q, margin)))
                        continue;

                    // Clear of what was placed in the last beat (except the note the stream starts
                    // from) and of its own notes three or more steps back.
                    double clearance = path.Min(q => ClearanceExcept(q, start, recent, timeUs, beatLengthUs));
                    for (int a = 0; a < path.Count; a++)
                    {
                        for (int b = a + 3; b < path.Count; b++)
                            clearance = Math.Min(clearance, Distance(path[a], path[b]) * 1.2);
                    }

                    if (clearance >= radius * 1.2)
                        return new Queue<Vector2D>(path);

                    if (clearance > bestClearance)
                    {
                        bestClearance = clearance;
                        best = path;
                    }
                }
            }
        }

        if (best is not null)
            return new Queue<Vector2D>(best);

        // Nothing fits: head for the centre and bounce off the edges, so the stream stays on screen.
        var line = new Queue<Vector2D>();
        var p = start;
        double dir = toCentre;
        for (int k = 1; k <= total; k++)
        {
            var q = new Vector2D(p.X + step * Math.Cos(dir), p.Y + step * Math.Sin(dir));
            if (q.X < margin || q.X > width - margin)
                dir = Math.PI - dir;
            if (q.Y < margin || q.Y > height - margin)
                dir = -dir;
            p = new Vector2D(Math.Clamp(p.X + step * Math.Cos(dir), margin, width - margin), Math.Clamp(p.Y + step * Math.Sin(dir), margin, height - margin));
            line.Enqueue(p);
        }

        return line;
    }

    private static double ClearanceExcept(Vector2D p, Vector2D except, List<Recent> recent, long timeUs, double windowUs)
    {
        double min = double.PositiveInfinity;

        for (int i = recent.Count - 1; i >= 0 && timeUs - recent[i].TimeUs <= windowUs * 1.01; i--)
        {
            foreach (var q in recent[i].Points)
            {
                if (q != except)
                    min = Math.Min(min, Distance(p, q));
            }
        }

        return min;
    }

    /// <summary>For each 1/4 note, how many 1/4 notes its run has (0 for other events).</summary>
    private static int[] QuarterRunLengths(IReadOnlyList<PlannedEvent> events)
    {
        var total = new int[events.Count];
        int i = 1;

        while (i < events.Count)
        {
            bool IsQuarter(int k) => events[k - 1].Kind == EventKind.Attack && events[k].Kind == EventKind.Attack && events[k].GridIndex - events[k - 1].EndGridIndex == 1;

            if (!IsQuarter(i))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < events.Count && IsQuarter(i))
                i++;

            for (int k = start; k < i; k++)
                total[k] = i - start;
        }

        return total;
    }

    /// <summary>
    /// Combos follow the music in half-bar units (humans in the training split: median 4 objects):
    /// a new combo on beat 1 or 3 once the current one has three objects, after a pause, never over 8.
    /// </summary>
    private static bool StartsCombo(PlannedEvent e, PlannedEvent previous, int gapGrid, int comboLength)
    {
        int halfBar = (int)Math.Floor(e.GridIndex / (RhythmSelection.GridPerBar / 2.0));
        int previousHalfBar = (int)Math.Floor(previous.GridIndex / (RhythmSelection.GridPerBar / 2.0));
        bool halfBarBoundary = halfBar != previousHalfBar;
        return gapGrid >= 2 * RhythmSelection.GridPerBeat || comboLength >= 8 || (halfBarBoundary && comboLength >= 3);
    }

    /// <summary>
    /// Turn per move. Closed shapes are slightly off their polygon so they rotate as they repeat
    /// (rotating jump patterns) instead of landing on the same few spots again.
    /// </summary>
    private static double Turn(Shape shape, int index) => shape switch
    {
        Shape.Triangle => Radians(128),
        Shape.Square => Radians(95),
        Shape.Star => Radians(150),
        // Back and forth with a 30° alternating tilt: enough that every second object clears the one a
        // beat earlier instead of half-covering it (a 15° tilt left 35% overlaps).
        Shape.BackAndForth => Radians(index % 2 == 0 ? 150 : 210),
        Shape.Zigzag => Radians(index % 2 == 0 ? 135 : -135),
        Shape.Alternate => Radians(180),
        _ => Radians(30),
    };

    /// <summary>
    /// The intended move first; if it leaves the playfield, the mirrored move (keeps the shape, flips
    /// the rotation); then progressively rotated moves. Candidates overlapping recent objects are
    /// rejected. Returns the position, the new heading and whether the pattern was mirrored.
    /// </summary>
    private static (Vector2D, double, bool) Step(Vector2D from, double heading, double turn, double distance, double margin, double radius,
                                                 List<Recent> recent, long timeUs, double beatLengthUs, long comboStartUs)
    {
        var tries = new List<(double Turn, bool Mirrored)> { (turn, false), (-turn, true) };

        for (int k = 1; k <= 8; k++)
        {
            tries.Add((turn + Radians(20 * k), false));
            tries.Add((turn - Radians(20 * k), false));
            tries.Add((-turn + Radians(20 * k), true));
            tries.Add((-turn - Radians(20 * k), true));
        }

        (Vector2D Point, double Heading, bool Mirrored, double Score) best = (from, heading, false, double.NegativeInfinity);

        // Same distance first (visual spacing consistency), then slightly shorter or longer.
        var attempts = from scale in new[] { 1.0, 0.85, 1.15, 0.7 }
                       from tr in tries
                       select (tr.Turn, tr.Mirrored, Length: distance * scale);

        foreach (var (t, mirrored, length) in attempts)
        {
            double angle = heading + t;
            var p = new Vector2D(from.X + length * Math.Cos(angle), from.Y + length * Math.Sin(angle));

            if (!Inside(p, margin))
                continue;

            double score = Fit(p, recent, timeUs, beatLengthUs, comboStartUs, radius);

            if (score >= 2)
                return (p, angle, mirrored);

            if (score > best.Score)
                best = (p, angle, mirrored, score);
        }

        if (!double.IsNegativeInfinity(best.Score))
            return (best.Point, best.Heading, best.Mirrored);

        double toCentre = Math.Atan2(192 - from.Y, 256 - from.X);
        double d = Math.Min(distance, Math.Sqrt(Math.Pow(256 - from.X, 2) + Math.Pow(192 - from.Y, 2)));
        return (new Vector2D(from.X + d * Math.Cos(toCentre), from.Y + d * Math.Sin(toCentre)), toCentre, false);
    }

    /// <summary>
    /// How well a point keeps clear of other objects (heads and slider bodies): 2 when nothing from
    /// the current combo or the last two beats is close, 1 when only the last beat is clear (Insane
    /// criteria forbid full overlaps of close objects), below 1 otherwise, growing with the distance.
    /// Numbers of one combo stacking on each other read badly even when the rules allow it.
    /// </summary>
    private static double Fit(Vector2D p, List<Recent> recent, long timeUs, double beatLengthUs, long comboStartUs, double radius)
    {
        double near = Clearance(p, recent, timeUs, beatLengthUs);
        double far = Clearance(p, recent, timeUs, Math.Max(2 * beatLengthUs, timeUs - comboStartUs));

        if (near < radius * 1.2)
            return near / (radius * 1.2);

        return far >= radius * 2 ? 2 : 1 + far / (radius * 2);
    }

    private static double Clearance(Vector2D p, List<Recent> recent, long timeUs, double windowUs)
    {
        double min = double.PositiveInfinity;

        for (int i = recent.Count - 1; i >= 0 && timeUs - recent[i].TimeUs <= windowUs * 1.01; i--)
        {
            foreach (var q in recent[i].Points)
                min = Math.Min(min, Distance(p, q));
        }

        return min;
    }

    /// <summary>
    /// A partial overlap with an earlier head of the same combo reads as sloppy; mappers either
    /// stack exactly (back-and-forth "stacks" in the osu! wiki) or keep clear. Heads more than a beat
    /// old that overlap by more than a quarter (closer than 1.5 radii) are snapped onto exactly. Recent objects are never stacked on.
    /// </summary>
    private static Vector2D SnapToStack(Vector2D p, List<Recent> recent, long timeUs, double beatLengthUs, long comboStartUs, double radius)
    {
        for (int i = recent.Count - 1; i >= 0 && recent[i].TimeUs >= comboStartUs; i--)
        {
            if (timeUs - recent[i].TimeUs <= beatLengthUs * 1.01)
                continue;

            var head = recent[i].Points[0];

            if (Distance(p, head) < radius * 1.5)
                return head;
        }

        return p;
    }

    private sealed record SliderPlacement(SliderPathIr Path, Vector2D End, double StartHeading, double ExitHeading, Vector2D[] Body);

    /// <summary>
    /// A slider that continues the current movement and bends with the flow (straight when short).
    /// Tries the intended direction first, then rotations, keeping the body inside the playfield
    /// and off anything from the last beat.
    /// </summary>
    private static SliderPlacement SliderShape(Vector2D head, double heading, int flowSign, double length, bool curved, double margin, double radius,
                                               List<Recent> recent, long timeUs, double beatLengthUs, long comboStartUs)
    {
        double bend = !curved || length < 70 ? 0 : flowSign * 0.18;
        SliderPlacement? best = null;
        double bestClearance = double.NegativeInfinity;

        for (int k = 0; k < 18; k++)
        {
            double offset = (k % 2 == 0 ? 1 : -1) * Radians(20 * ((k + 1) / 2));
            double angle = heading + offset;
            var (control, end) = Bezier(angle, bend, length);
            var body = Sample(control, end, 12).Select(q => new Vector2D(head.X + q.X, head.Y + q.Y)).ToArray();

            if (!body.All(q => Inside(q, margin)))
                continue;

            double clearance = body.Skip(2).Select(q => Fit(q, recent, timeUs, beatLengthUs, comboStartUs, radius)).DefaultIfEmpty(2).Min();
            var last = body[^1];
            var beforeLast = body[^2];
            double exit = Math.Atan2(last.Y - beforeLast.Y, last.X - beforeLast.X);
            double start = Math.Atan2(body[1].Y - body[0].Y, body[1].X - body[0].X);

            var path = new SliderPathIr
            {
                ControlPoints = bend == 0
                    ? [Point(0, 0, SegmentType.Linear), Point(end.X, end.Y)]
                    : [Point(0, 0, SegmentType.Bezier), Point(control.X, control.Y), Point(end.X, end.Y)],
            };

            var placement = new SliderPlacement(path, last, start, exit, body);

            if (clearance >= 2)
                return placement;

            if (clearance > bestClearance)
            {
                best = placement;
                bestClearance = clearance;
            }
        }

        if (best is not null)
            return best;

        // Fall back to a straight slider towards the centre, which always fits for MVP lengths.
        double toCentre = Math.Atan2(192 - head.Y, 256 - head.X);
        var straightEnd = new Vector2D(Math.Round(length * Math.Cos(toCentre), 1), Math.Round(length * Math.Sin(toCentre), 1));
        var tail = new Vector2D(head.X + straightEnd.X, head.Y + straightEnd.Y);
        return new SliderPlacement(new SliderPathIr { ControlPoints = [Point(0, 0, SegmentType.Linear), Point(straightEnd.X, straightEnd.Y)] },
                                   tail, toCentre, toCentre, [head, tail]);
    }

    /// <summary>Quadratic Bézier with its control point offset sideways by bend × chord, scaled to the arc length.</summary>
    private static (Vector2D Control, Vector2D End) Bezier(double angle, double bend, double length)
    {
        var end = new Vector2D(Math.Cos(angle), Math.Sin(angle));
        var control = new Vector2D(end.X / 2 - bend * end.Y, end.Y / 2 + bend * end.X);
        double scale = length / ArcLength(control, end);
        return (new Vector2D(Math.Round(control.X * scale, 1), Math.Round(control.Y * scale, 1)),
                new Vector2D(Math.Round(end.X * scale, 1), Math.Round(end.Y * scale, 1)));
    }

    private static IEnumerable<Vector2D> Sample(Vector2D control, Vector2D end, int count)
    {
        for (int i = 0; i <= count; i++)
        {
            double t = i / (double)count;
            double b = 2 * (1 - t) * t, c = t * t;
            yield return new Vector2D(b * control.X + c * end.X, b * control.Y + c * end.Y);
        }
    }

    private static double ArcLength(Vector2D control, Vector2D end)
    {
        var points = Sample(control, end, 64).ToArray();
        double sum = 0;

        for (int i = 1; i < points.Length; i++)
            sum += Distance(points[i], points[i - 1]);

        return sum;
    }

    private static PathControlPointIr Point(double x, double y, SegmentType? type = null) => new() { X = x, Y = y, SegmentType = type };

    private static bool Inside(Vector2D p, double margin) => p.X >= margin && p.X <= width - margin && p.Y >= margin && p.Y <= height - margin;

    private static double Distance(Vector2D a, Vector2D b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
