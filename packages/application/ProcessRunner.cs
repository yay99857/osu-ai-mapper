using System.Diagnostics;
using System.Text;

namespace OsuAi.Application;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs a tool asynchronously, reading both streams without blocking. Cancellation kills the whole
/// process tree (uv starts Python as a child) and waits for it to exit before returning, so no file
/// stays locked by a process the caller has given up on.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        // Python tools print UTF-8 regardless of the console code page.
        start.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = start };

        if (!process.Start())
            throw new InvalidOperationException($"Could not start '{fileName}'.");

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}
