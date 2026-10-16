using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32;

namespace OsuAi.Installer;

/// <summary>
/// Installs the osu-ai-mapper desktop app for the current user: the app (self-contained, carried
/// inside this program) goes to %LOCALAPPDATA%\Programs\osu-ai-mapper, with Start menu and desktop
/// shortcuts and an entry in "Apps and features" to remove it. The Python tools and Mapperatorinator
/// stay in the repository the installer was built from (several gigabytes of environments); the
/// installed app finds them through osu-ai-mapper.json beside it.
///
///   osu-ai-mapper-setup [--target dir] [--repo dir] [--uv path] [--no-shortcuts] [--no-register] [--quiet]
///   osu-ai-mapper-setup --uninstall [--target dir]
/// </summary>
public static class Program
{
    private const string AppName = "osu-ai-mapper";
    private const string ExeName = "osu-ai-mapper.exe";
    private const string ConfigName = "osu-ai-mapper.json";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\osu-ai-mapper";

    /// <summary>Set when the user chose to open the app at the end: the window then closes on its own.</summary>
    private static bool launched;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        bool quiet = args.Contains("--quiet");

        try
        {
            var flags = Flags(args);
            string target = Path.GetFullPath(flags.GetValueOrDefault("target") ?? DefaultTarget);
            int code = flags.ContainsKey("uninstall") ? Uninstall(target, flags) : Install(target, flags);
            if (!quiet && !launched)
                Pause();
            return code;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or ArgumentException)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine($"Não foi possível concluir: {e.Message}");
            Console.ResetColor();
            if (!quiet)
                Pause();
            return 1;
        }
    }

    private static string DefaultTarget =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{AppName}.lnk");

    private static string DesktopShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk");

    private static int Install(string target, Dictionary<string, string> flags)
    {
        var settings = Settings();
        string repo = Path.GetFullPath(flags.GetValueOrDefault("repo") ?? settings.RepositoryRoot);
        string? uv = flags.GetValueOrDefault("uv") ?? settings.Uv;

        Title($"Instalando o osu-ai-mapper {settings.Version}");
        Step("Pasta do programa", target);
        Step("Ferramentas (Python, Mapperatorinator)", repo);

        if (!File.Exists(Path.Combine(repo, "OsuAiMapper.slnx")))
            throw new InvalidOperationException($"'{repo}' não é o repositório do osu-ai-mapper. Use --repo <pasta>.");
        if (!File.Exists(Path.Combine(repo, "artifacts", "external", "Mapperatorinator", ".venv", "Scripts", "python.exe")))
            Warn("O Mapperatorinator não está instalado nesse repositório: o app vai oferecer só as regras e o Pulso.");
        if (uv is null || !File.Exists(uv))
        {
            uv = FindOnPath("uv.exe");
            if (uv is null)
                Warn("O 'uv' não foi encontrado: a análise de áudio não vai funcionar até ele estar no PATH.");
        }

        StopIfRunning(target);
        using var payload = typeof(Program).Assembly.GetManifestResourceStream("payload.zip")
            ?? throw new InvalidOperationException("Este instalador foi compilado sem o app dentro. Gere-o com tools/installer/build.ps1.");

        // Only a folder that is ours (or empty) is replaced; a wrong --target never wipes anything else.
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any() && !File.Exists(Path.Combine(target, ConfigName)))
            throw new InvalidOperationException($"'{target}' já tem outros arquivos; escolha uma pasta vazia com --target.");

        Step("Copiando o programa", "");
        string staging = target + ".new";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
            zip.ExtractToDirectory(staging);
        File.WriteAllText(Path.Combine(staging, ConfigName), JsonSerializer.Serialize(new { repositoryRoot = repo, uv }, new JsonSerializerOptions { WriteIndented = true }));
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.Move(staging, target);
        string exe = Path.Combine(target, ExeName);

        if (!flags.ContainsKey("no-shortcuts"))
        {
            Step("Atalhos", "Menu Iniciar e área de trabalho");
            Shortcut(StartMenuShortcut, exe, target);
            Shortcut(DesktopShortcut, exe, target);
        }

        if (!flags.ContainsKey("no-register"))
        {
            Step("Registrando em \"Aplicativos e recursos\"", "");
            Register(target, exe, settings.Version);
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine();
        Console.WriteLine(flags.ContainsKey("no-shortcuts")
            ? $"Pronto! O osu-ai-mapper está em {exe}."
            : "Pronto! O osu-ai-mapper está no Menu Iniciar e na área de trabalho.");
        Console.ResetColor();

        if (!flags.ContainsKey("quiet") && !flags.ContainsKey("no-shortcuts") && Ask("Abrir agora? (S/n) "))
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = target });
            launched = true;
        }
        return 0;
    }

    private static int Uninstall(string target, Dictionary<string, string> flags)
    {
        Title("Removendo o osu-ai-mapper");
        if (Directory.Exists(target) && !File.Exists(Path.Combine(target, ConfigName)))
            throw new InvalidOperationException($"'{target}' não parece uma instalação do osu-ai-mapper; nada foi apagado.");
        StopIfRunning(target);
        foreach (string link in new[] { StartMenuShortcut, DesktopShortcut })
        {
            if (File.Exists(link))
                File.Delete(link);
        }
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        if (!flags.ContainsKey("no-register"))
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Console.WriteLine("Removido. Seus projetos e preferências não foram tocados.");
        return 0;
    }

    /// <summary>The entry in Apps and features. Removing runs this same logic, written as PowerShell.</summary>
    private static void Register(string target, string exe, string version)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
        string remove =
            $"Get-Process -Name {Quote(AppName)} -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -like {Quote(target + "\\*")} }} | Stop-Process -Force; " +
            $"Start-Sleep -Milliseconds 500; " +
            $"Remove-Item -LiteralPath {Quote(StartMenuShortcut)},{Quote(DesktopShortcut)} -ErrorAction SilentlyContinue; " +
            $"Remove-Item -LiteralPath {Quote(target)} -Recurse -Force -ErrorAction SilentlyContinue; " +
            $"Remove-Item -Path {Quote("HKCU:\\" + UninstallKey)} -Recurse -ErrorAction SilentlyContinue";
        long sizeKb = new DirectoryInfo(target).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", AppName);
        key.SetValue("DisplayIcon", exe);
        key.SetValue("InstallLocation", target);
        key.SetValue("UninstallString", $"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{remove}\"");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
    }

    private static void Shortcut(string link, string exe, string workingDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.IconLocation = exe + ",0";
            shortcut.Description = "Gera mapas de osu! com o Mapperatorinator";
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>Closes a running copy of the installed app, which would hold its files open.</summary>
    private static void StopIfRunning(string target)
    {
        foreach (var process in Process.GetProcessesByName(AppName))
        {
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            if (path is null || !path.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            Step("Fechando o osu-ai-mapper aberto", "");
            process.CloseMainWindow();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
    }

    private sealed record InstallSettings(string RepositoryRoot, string? Uv, string Version);

    private static InstallSettings Settings()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("install.json")
            ?? throw new InvalidOperationException("Este instalador foi compilado sem as configurações. Gere-o com tools/installer/build.ps1.");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        return new InstallSettings(
            root.GetProperty("repositoryRoot").GetString()!,
            root.TryGetProperty("uv", out var uv) ? uv.GetString() : null,
            root.TryGetProperty("version", out var version) ? version.GetString() ?? "dev" : "dev");
    }

    private static string? FindOnPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim(), file))
            .FirstOrDefault(File.Exists);

    private static Dictionary<string, string> Flags(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            string name = args[i][2..];
            flags[name] = name is "target" or "repo" or "uv" && i + 1 < args.Length ? args[++i] : "";
        }
        return flags;
    }

    private static void Title(string text)
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(text);
        Console.WriteLine(new string('─', text.Length));
        Console.ResetColor();
    }

    private static void Step(string what, string detail) =>
        Console.WriteLine(detail.Length > 0 ? $"  • {what}: {detail}" : $"  • {what}");

    private static void Warn(string text)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  ! {text}");
        Console.ResetColor();
    }

    private static bool Ask(string question)
    {
        Console.Write(question);
        string? answer = Console.ReadLine();
        return answer is null || answer.Trim().Length == 0 || answer.Trim().StartsWith('s') || answer.Trim().StartsWith('S');
    }

    private static void Pause()
    {
        if (Console.IsInputRedirected)
            return;
        Console.WriteLine();
        Console.Write("Pressione Enter para fechar.");
        Console.ReadLine();
    }
}
