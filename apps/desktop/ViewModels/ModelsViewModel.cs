using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OsuAi.Application;
using OsuAi.Desktop.Services;

namespace OsuAi.Desktop.ViewModels;

/// <summary>One installed model as the manager window shows it.</summary>
public sealed partial class ModelRow(ModelInfo info, bool isDefault) : ObservableObject
{
    public ModelInfo Info { get; } = info;
    [ObservableProperty] private bool isDefault = isDefault;

    public string Name => Info.Name;
    public string File => OsuAi.Application.MapperatorinatorEngine.IsEngine(Info.Path) ? Info.Path : System.IO.Path.GetFileName(Info.Path);
    public string Details =>
        string.Join(" · ", new[]
        {
            Info.Features.Length > 0 ? Info.Features : null,
            Info.NeedsStems ? "usa voz separada" : null,
            Info.Styles.Count > 0 ? "estilos: " + string.Join(", ", Info.Styles) : null,
            Info.SizeBytes > 0 ? $"{Info.SizeBytes / 1e6:0.0} MB" : null,
            Info.Modified > DateTime.MinValue ? Info.Modified.ToString("dd/MM HH:mm", CultureInfo.CurrentCulture) : null,
        }.Where(s => s is not null));
    public string Summary => Info.Summary;
    public bool HasSummary => Info.Summary.Length > 0;
    public bool HasBackup => Info.HasBackup;
}

/// <summary>
/// The model manager: installed models and their cards, the default model for new projects,
/// learning rounds (osu-ai-learn) with live progress, their history, restoring a model's previous
/// version, and the state of the unattended jobs. The work itself is in ModelAdmin.
/// </summary>
public sealed partial class ModelsViewModel : ObservableObject
{
    private readonly ModelAdmin admin;
    private readonly Preferences preferences;
    private CancellationTokenSource? learning;

    public ModelsViewModel(ModelAdmin admin, Preferences preferences)
    {
        this.admin = admin;
        this.preferences = preferences;
        Refresh();
    }

    public ObservableCollection<ModelRow> Models { get; } = [];
    public ObservableCollection<ModelRow> LearnableModels { get; } = [];
    public ObservableCollection<string> History { get; } = [];
    public ObservableCollection<string> Jobs { get; } = [];
    public static IReadOnlyList<string> FocusNames { get; } = ["Todos os mapas", "Mapas adicionados por último", "Mapas de um mapper"];

    /// <summary>Set when the installed models or the default changed, so the main window reloads its list.</summary>
    public bool Changed { get; private set; }

    [ObservableProperty] private int learnModelIndex;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsMapper))]
    private int focusIndex;
    [ObservableProperty] private string mapperName = "";
    [ObservableProperty] private decimal? maps = 200;
    [ObservableProperty] private decimal? rounds = 30;
    [ObservableProperty] private bool installIfBetter = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartLearningCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelLearningCommand))]
    private bool isLearning;

    [ObservableProperty] private double learnPercent;
    [ObservableProperty] private string learnStage = "";
    [ObservableProperty] private string learnResult = "";
    [ObservableProperty] private string errorText = "";

    public bool NeedsMapper => FocusIndex == 2;
    public bool IsIdle => !IsLearning;

    [RelayCommand]
    public void Refresh()
    {
        string? chosen = Models.Count > 0 && LearnModelIndex >= 0 && LearnModelIndex < LearnableModels.Count ? LearnableModels[LearnModelIndex].Info.Path : null;
        Models.Clear();
        LearnableModels.Clear();
        foreach (var m in admin.Models())
        {
            var row = new ModelRow(m, string.Equals(m.Path, preferences.DefaultModel, StringComparison.OrdinalIgnoreCase));
            Models.Add(row);
            if (m.CanLearn)
                LearnableModels.Add(row);
        }

        int keep = LearnableModels.ToList().FindIndex(r => r.Info.Path == chosen);
        LearnModelIndex = keep >= 0 ? keep : Math.Max(0, LearnableModels.ToList().FindIndex(r => r.File == "pulso.joblib"));

        History.Clear();
        foreach (var r in admin.History().Take(30))
            History.Add($"{Stamp(r.Stamp)}  {Focus(r.Focus)}  {r.Maps} mapas  {r.ChangePercent:+000.00;-000.00}%  —  {r.Comment}");
        if (History.Count == 0)
            History.Add("Nenhuma rodada ainda.");

        Jobs.Clear();
        foreach (var j in admin.Jobs())
            Jobs.Add($"{j.Name}: {Stage(j.Stage)} — {j.Detail}" + (j.Updated is { } u ? $" (atualizado {u:dd/MM HH:mm})" : ""));
        if (Jobs.Count == 0)
            Jobs.Add("Nenhuma tarefa em segundo plano.");
    }

    [RelayCommand]
    private void SetDefault(ModelRow row)
    {
        preferences.DefaultModel = row.Info.Path;
        preferences.Save();
        foreach (var m in Models)
            m.IsDefault = m == row;
        Changed = true;
    }

    [RelayCommand]
    private void Restore(ModelRow row)
    {
        try
        {
            ModelAdmin.RestoreBackup(row.Info.Path);
            ErrorText = "";
            LearnResult = $"{row.Name}: versão anterior restaurada (a substituída ficou como {row.File}.undone).";
            Changed = true;
            Refresh();
        }
        catch (IOException e)
        {
            ErrorText = e.Message;
        }
    }

    private bool CanStart() => !IsLearning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartLearning()
    {
        if (LearnModelIndex < 0 || LearnModelIndex >= LearnableModels.Count)
        {
            ErrorText = "Escolha um modelo para treinar.";
            return;
        }
        string focus = FocusIndex switch { 1 => "recent", 2 => "mapper:" + MapperName.Trim(), _ => "all" };
        if (FocusIndex == 2 && MapperName.Trim().Length == 0)
        {
            ErrorText = "Escreva o nome do mapper.";
            return;
        }

        ErrorText = "";
        LearnResult = "";
        LearnPercent = 0;
        LearnStage = "iniciando…";
        IsLearning = true;
        learning = new CancellationTokenSource();
        try
        {
            var options = new LearnOptions(LearnableModels[LearnModelIndex].Info.Path, focus, (int)(Maps ?? 200), (int)(Rounds ?? 30), InstallIfBetter);
            var progress = new Progress<LearnProgress>(p => { LearnPercent = p.Percent; LearnStage = p.Stage; });
            var result = await admin.LearnAsync(options, progress, learning.Token);
            if (result.ChangePercent is { } change)
            {
                LearnPercent = 100;
                LearnStage = "pronto";
                LearnResult = $"Melhoria: {change:+000.00;-000.00}%{(result.Installed ? " — instalado" : InstallIfBetter ? " — não instalado (não melhorou)" : "")}"
                              + Environment.NewLine + result.Comment;
                if (result.Installed)
                    Changed = true;
            }
            else
            {
                ErrorText = "A rodada falhou." + Environment.NewLine + result.Output.Trim();
            }
        }
        catch (OperationCanceledException)
        {
            LearnStage = "cancelado";
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ErrorText = "Não foi possível iniciar o treino: " + e.Message;
        }
        finally
        {
            IsLearning = false;
            learning.Dispose();
            learning = null;
            Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(IsLearning))]
    private void CancelLearning() => learning?.Cancel();

    private static string Stamp(string stamp) =>
        DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToString("dd/MM HH:mm", CultureInfo.CurrentCulture) : stamp;

    private static string Focus(string focus) => focus switch
    {
        "all" => "todos",
        "recent" => "recentes",
        _ when focus.StartsWith("mapper:", StringComparison.Ordinal) => "mapper " + focus[7..],
        _ => focus,
    };

    private static string Stage(string stage) => stage switch
    {
        "waiting" => "esperando",
        "separating" => "separando voz e instrumentos",
        "separated" => "separação concluída",
        "training" => "treinando",
        "comparing" => "comparando modelos",
        "done" => "concluído",
        "failed" => "falhou",
        "retired" => "aposentada",
        _ => stage,
    };
}
