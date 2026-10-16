namespace OsuAi.BeatmapCore;

public enum Severity
{
    Info,
    Warning,

    /// <summary>The document is structurally invalid; nothing may be exported from it.</summary>
    Error,
}

public sealed record Diagnostic
{
    public required string Code { get; init; }
    public required Severity Severity { get; init; }
    public required string Message { get; init; }
    public IReadOnlyList<Guid> ObjectIds { get; init; } = [];
    public long? TimeUs { get; init; }

    public override string ToString()
    {
        string at = TimeUs is { } t ? $" @{t / 1000.0:0.###}ms" : "";
        return $"{Severity.ToString().ToLowerInvariant()} {Code}{at}: {Message}";
    }
}

public static class DiagnosticExtensions
{
    public static bool HasErrors(this IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(d => d.Severity == Severity.Error);
}
