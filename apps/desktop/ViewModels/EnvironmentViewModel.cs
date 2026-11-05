using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OsuAi.Application;

namespace OsuAi.Desktop.ViewModels;

/// <summary>One environment check as the window shows it.</summary>
public sealed record CheckRow(EnvironmentCheck Check)
{
    public string Mark => Check.Status switch { CheckStatus.Ok => "✓", CheckStatus.Warning => "!", _ => "✗" };
    public string Name => Check.Name;
    public string Detail => Check.Detail;
    public string Fix => Check.Status == CheckStatus.Ok ? "" : Check.Fix ?? "";
    public bool HasFix => Fix.Length > 0;
    public bool IsError => Check.Status == CheckStatus.Error;
    public bool IsWarning => Check.Status == CheckStatus.Warning;
}

/// <summary>The "Verificar ambiente" window: runs <see cref="EnvironmentDoctor"/> and lists what it found.</summary>
public sealed partial class EnvironmentViewModel(ToolEnvironment tools) : ObservableObject
{
    public ObservableCollection<CheckRow> Checks { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool isChecking;

    [ObservableProperty] private string summary = "";

    /// <summary>Raised with the result of every run, so the main window knows whether generating can start.</summary>
    public event Action<IReadOnlyList<EnvironmentCheck>>? Checked;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        IsChecking = true;
        Summary = "Verificando… (carregar o PyTorch leva alguns segundos)";
        Checks.Clear();
        try
        {
            var checks = await EnvironmentDoctor.RunAsync(tools, CancellationToken.None);
            foreach (var check in checks)
                Checks.Add(new CheckRow(check));
            Summary = SummaryOf(checks);
            Checked?.Invoke(checks);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Summary = "A verificação não terminou: " + e.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private bool CanRun() => !IsChecking;

    public static string SummaryOf(IReadOnlyList<EnvironmentCheck> checks)
    {
        int errors = checks.Count(c => c.Status == CheckStatus.Error);
        int warnings = checks.Count(c => c.Status == CheckStatus.Warning);
        string notes = warnings == 0 ? "" : warnings == 1 ? " 1 aviso." : $" {warnings} avisos.";
        return errors switch
        {
            0 => "Tudo pronto para gerar." + notes,
            1 => "1 problema impede a geração." + notes,
            _ => $"{errors} problemas impedem a geração." + notes,
        };
    }

    /// <summary>The errors with their fixes, as the main window shows them when a generation cannot start.</summary>
    public static string ErrorsOf(IReadOnlyList<EnvironmentCheck> checks) =>
        string.Join(Environment.NewLine, checks.Where(c => c.Status == CheckStatus.Error)
                                               .Select(c => $"{c.Name}: {c.Detail}" + (c.Fix is { Length: > 0 } fix ? $" → {fix}" : "")));
}
