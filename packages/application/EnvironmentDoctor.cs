using System.Globalization;

namespace OsuAi.Application;

public enum CheckStatus
{
    Ok,
    Warning,
    Error,
}

/// <summary>One finding about the machine: what was checked, what was found and, when it is not ok, what to do.</summary>
public sealed record EnvironmentCheck(string Name, CheckStatus Status, string Detail, string? Fix = null);

/// <summary>
/// Checks, before anything long starts, that the tools a generation needs are where the code looks
/// for them: uv, FFmpeg, the analysis environment, the Mapperatorinator checkout and its Python, the
/// weights, disk and memory, plus what the desktop app uses on this OS (audio player, .osz
/// association). Errors stop a generation; warnings make it slower or lose a convenience.
/// </summary>
public static class EnvironmentDoctor
{
    /// <summary>Below this much free disk a full song's revisions, stems and the first weights download may not fit.</summary>
    public const long MinFreeDiskBytes = 5L << 30;

    /// <summary>Below this much available memory Mapperatorinator on the CPU may swap or be killed.</summary>
    public const long MinAvailableMemoryBytes = 4L << 30;

    /// <summary>The Mapperatorinator revision this integration was last verified with, kept in the repository.</summary>
    public static string VerifiedRefFile(ToolEnvironment tools) => Path.Combine(tools.RepositoryRoot, "tools", "linux", "mapperatorinator.ref");

    public static async Task<IReadOnlyList<EnvironmentCheck>> RunAsync(ToolEnvironment tools, CancellationToken cancellationToken)
    {
        var checks = new List<EnvironmentCheck>
        {
            await VersionOfAsync("uv", tools.Uv, ["--version"], "Instale o uv: https://docs.astral.sh/uv/", cancellationToken).ConfigureAwait(false),
            await VersionOfAsync("FFmpeg", "ffmpeg", ["-version"], InstallHint("ffmpeg"), cancellationToken).ConfigureAwait(false),
            AnalysisEnvironment(tools),
        };

        checks.AddRange(await MapperatorinatorAsync(tools, cancellationToken).ConfigureAwait(false));
        checks.Add(Weights());
        checks.Add(Disk(tools.RepositoryRoot));
        if (Memory(ReadAvailableMemory()) is { } memory)
            checks.Add(memory);
        checks.AddRange(DesktopTools());
        if (OperatingSystem.IsLinux())
            checks.Add(await OszHandlerAsync(cancellationToken).ConfigureAwait(false));
        return checks;
    }

    private static string SetupHint => OperatingSystem.IsWindows()
        ? "Siga a seção 'Começar' do README para criar artifacts/external/Mapperatorinator/.venv"
        : "Rode tools/linux/setup.sh";

    private static string InstallHint(string package) => OperatingSystem.IsLinux()
        ? $"No Arch/Omarchy: sudo pacman -S --needed {package}"
        : $"Instale o {package} e deixe-o no PATH";

    private static async Task<EnvironmentCheck> VersionOfAsync(string name, string program, string[] arguments, string fix, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(program, arguments, cancellationToken).ConfigureAwait(false);
            string first = FirstLine(result.StandardOutput + result.StandardError);
            return result.ExitCode == 0
                ? new EnvironmentCheck(name, CheckStatus.Ok, first)
                : new EnvironmentCheck(name, CheckStatus.Error, $"'{program}' saiu com código {result.ExitCode}: {first}", fix);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new EnvironmentCheck(name, CheckStatus.Error, $"'{program}' não foi encontrado", fix);
        }
    }

    internal static EnvironmentCheck AnalysisEnvironment(ToolEnvironment tools) =>
        File.Exists(ToolEnvironment.VenvPython(tools.AnalyzerProject))
            ? new EnvironmentCheck("Análise de áudio", CheckStatus.Ok, "ambiente em engines/audio-analysis/.venv")
            : new EnvironmentCheck("Análise de áudio", CheckStatus.Warning, "ambiente ainda não criado; a primeira análise vai criá-lo e demorar mais",
                "uv sync --locked --project engines/audio-analysis");

    internal static async Task<IReadOnlyList<EnvironmentCheck>> MapperatorinatorAsync(ToolEnvironment tools, CancellationToken cancellationToken)
    {
        const string name = "Mapperatorinator";
        if (!File.Exists(Path.Combine(tools.MapperatorinatorRoot, "inference.py")))
            return [new EnvironmentCheck(name, CheckStatus.Error, $"checkout não encontrado em {tools.MapperatorinatorRoot}", SetupHint)];
        if (!File.Exists(tools.MapperatorinatorPython))
            return [new EnvironmentCheck(name, CheckStatus.Error, $"checkout sem ambiente Python ({tools.MapperatorinatorPython})", SetupHint)];

        var checks = new List<EnvironmentCheck>();
        const string probe = """
            import torch, hydra, transformers, slider, pydub
            print(torch.__version__, torch.cuda.get_device_name(0) if torch.cuda.is_available() else "cpu")
            """;
        var result = await ProcessRunner.RunAsync(tools.MapperatorinatorPython, ["-c", probe], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            checks.Add(new EnvironmentCheck(name, CheckStatus.Error, "o ambiente não importa suas dependências: " + LastLine(result.StandardError), SetupHint));
        }
        else
        {
            var parts = FirstLine(result.StandardOutput).Split(' ', 2);
            string device = parts.Length > 1 ? parts[1] : "cpu";
            checks.Add(device == "cpu"
                ? new EnvironmentCheck(name, CheckStatus.Warning, $"PyTorch {parts[0]} sem GPU: cerca de dez minutos por minuto de música",
                    "Com uma GPU suportada, reinstale o PyTorch com TORCH_INDEX (veja tools/linux/setup.sh)")
                : new EnvironmentCheck(name, CheckStatus.Ok, $"PyTorch {parts[0]} na GPU {device}"));
        }

        if (await RevisionCheckAsync(tools, cancellationToken).ConfigureAwait(false) is { } revision)
            checks.Add(revision);
        return checks;
    }

    private static async Task<EnvironmentCheck?> RevisionCheckAsync(ToolEnvironment tools, CancellationToken cancellationToken)
    {
        string file = VerifiedRefFile(tools);
        if (!File.Exists(file))
            return null;
        try
        {
            var head = await ProcessRunner.RunAsync("git", ["-C", tools.MapperatorinatorRoot, "rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
            return head.ExitCode == 0 ? Revision(FirstLine(head.StandardOutput), File.ReadAllText(file)) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // No git: the revision cannot be told, which is not a problem by itself.
        }
    }

    /// <summary>Whether the checkout is at the revision the integration was verified with.</summary>
    internal static EnvironmentCheck Revision(string current, string verified)
    {
        current = current.Trim();
        verified = verified.Trim();
        return string.Equals(current, verified, StringComparison.OrdinalIgnoreCase)
            ? new EnvironmentCheck("Revisão do Mapperatorinator", CheckStatus.Ok, $"{Short(current)}, a verificada")
            : new EnvironmentCheck("Revisão do Mapperatorinator", CheckStatus.Warning,
                $"checkout em {Short(current)}; a integração foi verificada com {Short(verified)}",
                $"git -C artifacts/external/Mapperatorinator checkout {Short(verified)}, ou verifique a nova revisão e atualize tools/linux/mapperatorinator.ref");
    }

    private static EnvironmentCheck Weights() =>
        Directory.Exists(Path.Combine(MapperatorinatorEngine.WeightsFolder, "snapshots"))
            ? new EnvironmentCheck("Pesos v32", CheckStatus.Ok, MapperatorinatorEngine.WeightsFolder)
            : new EnvironmentCheck("Pesos v32", CheckStatus.Warning, "ainda não baixados; a primeira geração baixa cerca de 1 GB",
                "Nada a fazer, só espere o download na primeira vez");

    private static EnvironmentCheck Disk(string path)
    {
        long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
        return Space(free);
    }

    internal static EnvironmentCheck Space(long freeBytes) => freeBytes >= MinFreeDiskBytes
        ? new EnvironmentCheck("Disco", CheckStatus.Ok, $"{Gib(freeBytes)} livres")
        : new EnvironmentCheck("Disco", CheckStatus.Warning, $"só {Gib(freeBytes)} livres", "Libere espaço antes de gerar uma música inteira");

    internal static EnvironmentCheck? Memory(long? availableBytes) => availableBytes switch
    {
        null => null,
        >= MinAvailableMemoryBytes => new EnvironmentCheck("Memória", CheckStatus.Ok, $"{Gib(availableBytes.Value)} disponíveis"),
        _ => new EnvironmentCheck("Memória", CheckStatus.Warning, $"só {Gib(availableBytes.Value)} disponíveis; a geração pode ficar lenta ou ser encerrada",
            "Feche programas pesados e rode um processo pesado por vez"),
    };

    /// <summary>MemAvailable from /proc/meminfo, on Linux; null where it cannot be read.</summary>
    internal static long? ReadAvailableMemory(string meminfo = "/proc/meminfo")
    {
        if (!File.Exists(meminfo))
            return null;
        try
        {
            return ParseMemAvailable(File.ReadAllText(meminfo));
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static long? ParseMemAvailable(string meminfo)
    {
        foreach (string line in meminfo.Split('\n'))
        {
            if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                continue;
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 2 && long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long kib) ? kib * 1024 : null;
        }
        return null;
    }

    /// <summary>What the desktop app calls on this OS besides the generation tools.</summary>
    private static IEnumerable<EnvironmentCheck> DesktopTools()
    {
        if (OperatingSystem.IsWindows())
            yield break;

        yield return FindOnPath("mpv") is { } mpv
            ? new EnvironmentCheck("Reprodução de áudio", CheckStatus.Ok, mpv)
            : new EnvironmentCheck("Reprodução de áudio", CheckStatus.Warning, "mpv não encontrado; o app não toca a música", InstallHint("mpv"));
        yield return FindOnPath("notify-send") is not null
            ? new EnvironmentCheck("Aviso de fim de geração", CheckStatus.Ok, "notify-send")
            : new EnvironmentCheck("Aviso de fim de geração", CheckStatus.Warning, "notify-send não encontrado; sem notificação ao terminar", InstallHint("libnotify"));
    }

    private static async Task<EnvironmentCheck> OszHandlerAsync(CancellationToken cancellationToken)
    {
        const string name = "Abrir no osu!";
        if (FindOnPath("xdg-mime") is null)
            return new EnvironmentCheck(name, CheckStatus.Warning, "xdg-mime não encontrado; não dá para saber quem abre .osz", InstallHint("xdg-utils"));
        try
        {
            var result = await ProcessRunner.RunAsync("xdg-mime", ["query", "default", "application/x-osu-beatmap-archive"], cancellationToken).ConfigureAwait(false);
            string handler = FirstLine(result.StandardOutput);
            return handler.Length > 0
                ? new EnvironmentCheck(name, CheckStatus.Ok, $".osz abre com {handler}")
                : new EnvironmentCheck(name, CheckStatus.Warning, "nenhum programa associado a .osz", "Instale o osu!lazer (pacote osu-lazer) ou associe-o a .osz");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new EnvironmentCheck(name, CheckStatus.Warning, "xdg-mime não pôde ser executado");
        }
    }

    /// <summary>The full path of an executable on PATH, honouring PATHEXT on Windows.</summary>
    public static string? FindOnPath(string program)
    {
        string[] extensions = OperatingSystem.IsWindows()
            ? [.. (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries), ""]
            : [""];
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ext in extensions)
            {
                string candidate = Path.Combine(dir, program + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static string FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    private static string LastLine(string text) => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "(sem saída)";

    private static string Short(string revision) => revision.Length > 7 ? revision[..7] : revision;

    private static string Gib(long bytes) => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GiB";
}
