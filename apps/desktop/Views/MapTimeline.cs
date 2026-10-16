using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OsuAi.Application;

namespace OsuAi.Desktop.Views;

/// <summary>What the timeline draws: the song's loudness, the map's objects and what the checks found.</summary>
public sealed record TimelineData(
    double DurationMs,
    IReadOnlyList<(double TimeMs, double Energy)> Energy,
    IReadOnlyList<double> ObjectTimesMs,
    IReadOnlyList<MapIssue> Issues,
    (long StartMs, long EndMs)? PreviewSpan);

/// <summary>
/// The whole song at a glance: loudness as a soft area, note density as pink bars over it, problems
/// as marks along the top. Stretches where the music plays and the map is silent stand out at once.
/// </summary>
public sealed class MapTimeline : Control
{
    public static readonly StyledProperty<TimelineData?> DataProperty =
        AvaloniaProperty.Register<MapTimeline, TimelineData?>(nameof(Data));

    static MapTimeline() => AffectsRender<MapTimeline>(DataProperty);

    public TimelineData? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    private static readonly IBrush energy_brush = new SolidColorBrush(Color.Parse("#3A3446"));
    private static readonly IBrush notes_brush = new SolidColorBrush(Color.Parse("#FF66AA"));
    private static readonly IBrush preview_brush = new SolidColorBrush(Color.Parse("#22FFFFFF"));
    private static readonly IBrush problem_brush = new SolidColorBrush(Color.Parse("#FF6B6B"));
    private static readonly IBrush warning_brush = new SolidColorBrush(Color.Parse("#FFB454"));
    private static readonly IBrush notice_brush = new SolidColorBrush(Color.Parse("#A79FB2"));
    private static readonly IBrush label_brush = new SolidColorBrush(Color.Parse("#A79FB2"));

    public static IBrush BrushOf(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Problem => problem_brush,
        IssueSeverity.Warning => warning_brush,
        _ => notice_brush,
    };

    public override void Render(DrawingContext context)
    {
        var data = Data;
        double width = Bounds.Width, height = Bounds.Height;
        if (data is null || data.DurationMs <= 0 || width < 10 || height < 20)
            return;

        const double marks = 8, labels = 14;
        double top = marks + 2, bottom = height - labels, plot = bottom - top;
        double X(double ms) => Math.Clamp(ms / data.DurationMs, 0, 1) * width;

        if (data.PreviewSpan is { } span)
            context.FillRectangle(preview_brush, new Rect(X(span.StartMs), top, Math.Max(1, X(span.EndMs) - X(span.StartMs)), plot));

        // Loudness, one column per pixel, scaled to the loudest moment.
        int columns = (int)width;
        if (data.Energy.Count > 0)
        {
            var sums = new double[columns];
            var counts = new int[columns];
            foreach (var (time, energy) in data.Energy)
            {
                int c = Math.Clamp((int)X(time), 0, columns - 1);
                sums[c] += energy;
                counts[c]++;
            }
            double max = Enumerable.Range(0, columns).Where(c => counts[c] > 0).Select(c => sums[c] / counts[c]).DefaultIfEmpty(1).Max();
            for (int c = 0; c < columns; c++)
            {
                if (counts[c] == 0)
                    continue;
                double h = plot * (sums[c] / counts[c]) / Math.Max(1e-9, max);
                context.FillRectangle(energy_brush, new Rect(c, bottom - h, 1, h));
            }
        }

        // Notes per two pixels, scaled to the densest stretch, over the loudness.
        if (data.ObjectTimesMs.Count > 0)
        {
            int buckets = Math.Max(1, columns / 2);
            var density = new int[buckets];
            foreach (double t in data.ObjectTimesMs)
                density[Math.Clamp((int)(X(t) / 2), 0, buckets - 1)]++;
            double peak = Math.Max(1, density.Max());
            for (int b = 0; b < buckets; b++)
            {
                if (density[b] == 0)
                    continue;
                double h = Math.Max(2, plot * 0.85 * density[b] / peak);
                context.FillRectangle(notes_brush, new Rect(b * 2, bottom - h, 1.5, h));
            }
        }

        foreach (var issue in data.Issues.Where(i => i.EndMs is not null || i.TimeMs > 0))
        {
            double x0 = X(issue.TimeMs), x1 = issue.EndMs is { } end ? X(end) : x0 + 3;
            context.FillRectangle(BrushOf(issue.Severity), new Rect(x0, 0, Math.Max(3, x1 - x0), marks));
        }

        // Minute labels.
        var typeface = new Typeface("Segoe UI");
        for (double minute = 0; minute * 60_000 <= data.DurationMs; minute++)
        {
            double x = X(minute * 60_000);
            var text = new FormattedText($"{minute:0}:00", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, label_brush);
            if (x + text.Width <= width)
                context.DrawText(text, new Point(x + 2, bottom + 1));
            context.FillRectangle(label_brush, new Rect(x, bottom, 1, 4));
        }
    }
}
