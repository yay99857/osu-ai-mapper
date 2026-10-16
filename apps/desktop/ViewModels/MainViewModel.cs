using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OsuAi.Application;
using OsuAi.Desktop.Services;
using OsuAi.Desktop.Views;
using OsuAi.Generation;
using OsuAi.ProjectStore;

namespace OsuAi.Desktop.ViewModels;

/// <summary>One revision as the list shows it.</summary>
public sealed record RevisionRow(string Id, string Description, bool IsActive, bool InPackage, bool IsPreview)
{
    public string Label => IsActive ? $"● {Id}  {Description}" : $"   {Id}  {Description}";
    public bool CanPackage => !IsActive && !IsPreview;
}

/// <summary>One finding of the map checks, with its colour and time.</summary>
public sealed record IssueRow(string When, string Message, Avalonia.Media.IBrush Brush);

/// <summary>A style tag the model knows, with a Portuguese label.</summary>
public sealed partial class StyleTag(string value, string label, string tip) : ObservableObject
{
    public string Value { get; } = value;
    public string Label { get; } = label;
    public string Tip { get; } = tip;
    [ObservableProperty] private bool isSelected;
}

/// <summary>One difficulty to include when generating a mapset.</summary>
public sealed partial class MapsetChoice(string name, decimal stars, bool selected) : ObservableObject
{
    [ObservableProperty] private string name = name;
    [ObservableProperty] private decimal? stars = stars;
    [ObservableProperty] private bool isSelected = selected;
}

/// <summary>
/// The whole first-version flow: new or open project, music, metadata, generation settings,
/// analyse, generate, export. Every operation goes through <see cref="MappingService"/>; this class
/// only moves values between the form and the project.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly MappingService service;
    private readonly Preferences preferences;
    private readonly AudioPlayer player;
    private readonly ToolEnvironment tools;
    private Project? project;
    private CancellationTokenSource? running;

    public MainViewModel(MappingService service, Preferences preferences, AudioPlayer player, ToolEnvironment tools)
    {
        this.service = service;
        this.preferences = preferences;
        this.player = player;
        this.tools = tools;
        player.Stopped += () => IsPlaying = false;

        Models = LoadModels();
        modelIndex = DefaultModelIndex();

        foreach (var tag in StyleTags)
            tag.PropertyChanged += (_, _) => OnPropertyChanged(nameof(StyleSummary));
        foreach (var choice in MapsetChoices)
            choice.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(EstimateText)); GenerateMapsetCommand.NotifyCanExecuteChanged(); };
    }

    // ---- project ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProject), nameof(HasNoProject))]
    private string? projectPath;

    public bool HasProject => ProjectPath is not null;
    public bool HasNoProject => !HasProject;

    [ObservableProperty] private string audioName = "";
    [ObservableProperty] private string audioInfo = "";

    // ---- background image ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBackground), nameof(HasNoBackground))]
    private Bitmap? backgroundImage;

    [ObservableProperty] private string backgroundInfo = "";

    public bool HasBackground => BackgroundImage is not null;
    public bool HasNoBackground => !HasBackground;

    public static bool IsBackgroundFile(string path) => MappingService.BackgroundExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Copies the image into the project; it goes into the next exported package, no generation needed.</summary>
    public void SetBackground(string imagePath) => Run(() =>
    {
        string? cropped = BackgroundImages.FitTo169(imagePath);
        try
        {
            service.SetBackground(project!, cropped ?? imagePath);
        }
        finally
        {
            if (cropped is not null)
                Directory.Delete(Path.GetDirectoryName(cropped)!, recursive: true);
        }
        ShowBackground(project!);
        string note = cropped is null ? "" : " Recortada no centro para 16:9 (no máximo 1920×1080), como o osu! mostra.";
        InfoText = (HasRevision ? "Imagem de fundo definida. Ela entra no próximo .osz exportado, sem gerar de novo." : "Imagem de fundo definida.") + note;
    });

    [RelayCommand]
    private void RemoveBackground() => Run(() =>
    {
        service.ClearBackground(project!);
        ShowBackground(project!);
        InfoText = "Imagem de fundo removida.";
    });

    private void ShowBackground(Project opened)
    {
        BackgroundImage?.Dispose();
        BackgroundImage = null;
        BackgroundInfo = "";
        if (opened.Manifest.Background is not { } file || !File.Exists(opened.PathOf(file.RelativePath)))
            return;

        // Decoded from memory so the file stays free to be replaced or removed.
        using var stream = new MemoryStream(File.ReadAllBytes(opened.PathOf(file.RelativePath)));
        BackgroundImage = new Bitmap(stream);
        var size = BackgroundImage.PixelSize;
        string advice = size.Width < 1366 ? " · pequena: o osu! recomenda ao menos 1366×768" : "";
        BackgroundInfo = $"{file.OriginalName ?? Path.GetFileName(file.RelativePath)} · {size.Width}×{size.Height} · {file.ByteSize / 1024.0 / 1024.0:0.0} MB{advice}";
    }

    // ---- metadata form ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MetadataProblems))] private string title = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MetadataProblems))] private string? titleUnicode;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MetadataProblems))] private string artist = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MetadataProblems))] private string? artistUnicode;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MetadataProblems))] private string creator = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MetadataProblems))] private string difficultyName = "Insane";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MetadataProblems))] private string? source;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MetadataProblems))] private string tags = "";

    /// <summary>Problems with the form as it is now, in Portuguese for the user; empty when it can be saved.</summary>
    public string MetadataProblems => string.Join(Environment.NewLine, MetadataRules.Problems(MetadataRules.Normalise(FormMetadata())).Select(Translate));

    // ---- generation settings ----

    [ObservableProperty] private decimal? targetStars = 5.0m;

    /// <summary>0 automatic, 1 jumps, 2 streams (the order of the combo box).</summary>
    [ObservableProperty] private int styleIndex;

    public static IReadOnlyList<string> StyleNames { get; } = ["Automático", "Jumps (estilo dos mapas populares)", "Streams"];
    [ObservableProperty] private decimal? ar;
    [ObservableProperty] private decimal? od;
    [ObservableProperty] private decimal? cs;
    [ObservableProperty] private decimal? hp;
    [ObservableProperty] private decimal? seed = 7319;
    [ObservableProperty] private decimal? timingBpm;
    [ObservableProperty] private decimal? timingOffsetMs;

    /// <summary>Rules first, then the local models found under artifacts/models, best first.</summary>
    public IReadOnlyList<RhythmModelChoice> Models { get; private set; }
    public IReadOnlyList<string> ModelNames => Models.Select(m => m.Name).ToArray();

    private IReadOnlyList<RhythmModelChoice> LoadModels() => [new RhythmModelChoice("Regras (B1)", null, false), .. RhythmModelChoice.Discover(tools.RepositoryRoot)];

    /// <summary>The default chosen in the model manager, else the best local model: the first one after the rules.</summary>
    private int DefaultModelIndex()
    {
        int chosen = preferences.DefaultModel is { } path
            ? Models.ToList().FindIndex(m => RhythmModelChoice.Same(m.Path, path))
            : -1;
        return chosen >= 0 ? chosen : Models.Count > 1 ? 1 : 0;
    }

    /// <summary>The model manager window, over the same tools and preferences.</summary>
    public ModelsViewModel CreateModelsManager() => new(new ModelAdmin(tools), preferences);

    /// <summary>After the model manager changed models or the default: list them again, keeping the current choice.</summary>
    public void ReloadModels()
    {
        string? current = Models[Math.Clamp(ModelIndex, 0, Models.Count - 1)].Path;
        Models = LoadModels();
        OnPropertyChanged(nameof(Models));
        OnPropertyChanged(nameof(ModelNames));
        int keep = current is null ? 0 : Models.ToList().FindIndex(m => m.Path == current);
        ModelIndex = HasProject && keep >= 0 ? keep : DefaultModelIndex();
        OnPropertyChanged(nameof(StyleAvailable));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StyleAvailable), nameof(IsMapperatorinator), nameof(EstimateText))]
    private int modelIndex;

    partial void OnModelIndexChanged(int value) => PreviewCommand.NotifyCanExecuteChanged();

    public bool IsMapperatorinator => MapperatorinatorEngine.IsEngine(Models[Math.Clamp(ModelIndex, 0, Models.Count - 1)].Path);

    // ---- Mapperatorinator style ----

    /// <summary>
    /// The style tags most worth choosing from the ~70 v32 knows for osu!standard (the full list is
    /// in MapperatorinatorEngine.KnownDescriptors).
    /// </summary>
    public ObservableCollection<StyleTag> StyleTags { get; } =
    [
        new("skillset/jumps", "Jumps", "Muitos saltos, como nos mapas populares"),
        new("jumps/wide", "Jumps largos", "Saltos de um lado a outro da tela"),
        new("jumps/sharp", "Ângulos fechados", "Saltos que voltam em ângulo agudo"),
        new("skillset/streams", "Streams", "Sequências longas de 1/4"),
        new("streams/bursts", "Bursts", "Sequências curtas de 3 a 7 notas"),
        new("streams/spaced streams", "Streams espaçados", "Streams com distância entre as notas"),
        new("tech/flow", "Flow", "Movimento contínuo, sem quebras de direção"),
        new("skillset/tech", "Tech", "Ritmos e sliders variados, exige leitura"),
        new("sliders/complex slidershapes", "Sliders elaborados", "Sliders com formas trabalhadas"),
        new("sliders/high sv", "Sliders rápidos", "Velocidade de slider alta"),
        new("style/clean", "Limpo", "Visual organizado, sem sobreposição"),
        new("style/geometric", "Geométrico", "Formas regulares: triângulos, quadrados"),
        new("style/symmetrical", "Simétrico", "Padrões espelhados"),
        new("expression/simple", "Simples", "Ritmo direto, poucas variações"),
    ];

    public string StyleSummary => StyleTags.Count(t => t.IsSelected) is var n and > 0 ? $"{n} escolhido(s)" : "nenhum: o modelo decide";

    public static IReadOnlyList<string> MapperNames { get; } = ["Nenhum: o modelo decide", "Sotarks", "Outro (ID do osu!)"];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowCustomMapper), nameof(MapperHint))] private int mapperIndex;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MapperHint))] private decimal? customMapperId;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EstimateText))] private bool strongStyle;
    [ObservableProperty] private decimal? year = 2023;

    public bool ShowCustomMapper => MapperIndex == 2;

    private long? ChosenMapper => MapperIndex switch
    {
        1 => MapperatorinatorEngine.SotarksId,
        2 when CustomMapperId is { } id and > 0 => (long)id,
        _ => null,
    };

    public string MapperHint => ChosenMapper is not { } id
        ? ""
        : MapperatorinatorEngine.KnowsMapper(id) switch
        {
            true => "O modelo conhece este mapper.",
            false => "O modelo não aprendeu este mapper: o estilo dele vai ser ignorado.",
            null => "",
        };

    // ---- mapset ----

    public ObservableCollection<MapsetChoice> MapsetChoices { get; } =
    [
        new("Normal", 2.3m, false),
        new("Hard", 3.3m, true),
        new("Insane", 4.6m, true),
        new("Expert", 5.8m, false),
    ];

    // ---- time estimates ----

    private double songSeconds;

    /// <summary>Model loading and the reference map, before the first window: about a minute here.</summary>
    private const double StartupSeconds = 60;

    private double SecondsPerSongSecond => (preferences.MapperatorinatorSecondsPerSongSecond ?? 10) * (StrongStyle && (ChosenMapper is not null || StyleTags.Any(t => t.IsSelected)) ? 2 : 1);

    public string EstimateText
    {
        get
        {
            if (!IsMapperatorinator || songSeconds <= 0)
                return "";
            double full = StartupSeconds + SecondsPerSongSecond * songSeconds;
            double preview = StartupSeconds + SecondsPerSongSecond * Math.Min(30, songSeconds);
            int set = MapsetChoices.Count(c => c.IsSelected);
            return $"Tempo estimado: mapa inteiro ~{Minutes(full)}, prévia ~{Minutes(preview)}" + (set > 1 ? $", mapset de {set} ~{Minutes(full * set)}" : "") + ".";
        }
    }

    private static string Minutes(double seconds) => seconds < 90 ? "1 min" : seconds < 3600 ? $"{seconds / 60:0} min" : $"{seconds / 3600:0.#} h";

    /// <summary>Learns how fast this computer runs the model, from a run of that many song seconds.</summary>
    private void LearnSpeed(TimeSpan elapsed, double mappedSeconds, bool strong)
    {
        if (mappedSeconds < 20 || elapsed.TotalSeconds < StartupSeconds)
            return;
        double rate = (elapsed.TotalSeconds - StartupSeconds) / mappedSeconds / (strong ? 2 : 1);
        double old = preferences.MapperatorinatorSecondsPerSongSecond ?? rate;
        preferences.MapperatorinatorSecondsPerSongSecond = Math.Round(0.5 * old + 0.5 * rate, 2);
        preferences.Save();
        OnPropertyChanged(nameof(EstimateText));
    }

    public bool ModelAvailable => Models.Count > 1;
    public bool StyleAvailable => Models[Math.Clamp(ModelIndex, 0, Models.Count - 1)].Styled;

    // ---- status ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyseCommand), nameof(GenerateCommand), nameof(ExportCommand), nameof(OpenInOsuCommand), nameof(SaveMetadataCommand), nameof(CancelCommand), nameof(PreviewCommand), nameof(GenerateMapsetCommand))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private bool isBusy;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WindowTitle))] private double progressFraction;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(ProgressIndeterminate))] private bool hasProgressFraction;
    [ObservableProperty] private string remainingText = "";

    public bool ProgressIndeterminate => !HasProgressFraction;

    /// <summary>The percentage in the title shows on the taskbar while the user works elsewhere.</summary>
    public string WindowTitle => IsBusy && HasProgressFraction ? $"{ProgressFraction:0%} · osu-ai-mapper" : "osu-ai-mapper";

    /// <summary>A long operation finished (successfully or not), with what to tell the user.</summary>
    public event Action<string>? LongWorkFinished;

    public ObservableCollection<IssueRow> Issues { get; } = [];
    [ObservableProperty] private string reportSummary = "";
    [ObservableProperty] private TimelineData? timeline;

    [ObservableProperty] private string stageText = "";
    [ObservableProperty] private string errorText = "";
    [ObservableProperty] private string infoText = "";
    [ObservableProperty] private string analysisText = "Ainda não analisada.";
    [ObservableProperty] private string analysisWarning = "";
    [ObservableProperty] private string resultText = "";
    [ObservableProperty] private string resultWarning = "";
    [ObservableProperty] private bool isPlaying;

    public ObservableCollection<string> StageLog { get; } = [];
    public ObservableCollection<RevisionRow> Revisions { get; } = [];
    [ObservableProperty] private string packageSummary = "";

    public bool HasRevision => project?.ActiveRevision is not null;

    // ---- opening and creating ----

    /// <summary>Creates a project for an audio file in a new folder under <paramref name="parentFolder"/>.</summary>
    public void CreateProject(string audioPath, string parentFolder)
    {
        Run(() =>
        {
            var facts = AudioFacts.Read(audioPath);
            string name = SafeFolderName(facts.Artist is null ? facts.Title ?? Path.GetFileNameWithoutExtension(audioPath) : $"{facts.Artist} - {facts.Title}");
            string root = UniqueFolder(Path.Combine(parentFolder, name));

            var metadata = new ProjectMetadata
            {
                Title = facts.Title ?? Path.GetFileNameWithoutExtension(audioPath),
                Artist = facts.Artist ?? "Desconhecido",
                Creator = string.IsNullOrWhiteSpace(preferences.Creator) ? Environment.UserName : preferences.Creator,
                DifficultyName = "Insane",
            };

            // A new project starts with the default model (Mapperatorinator when installed), not the rules.
            var created = service.CreateProject(root, audioPath, metadata, new ProjectSettings { RhythmModel = Models[DefaultModelIndex()].Path });
            preferences.LastProjectsFolder = parentFolder;
            preferences.LastAudioFolder = Path.GetDirectoryName(audioPath);
            preferences.Save();
            Load(created);
            InfoText = "Projeto criado. Confira os metadados: título e artista vieram das tags do arquivo.";
        });
    }

    public void OpenProject(string root) => Run(() =>
    {
        Load(Project.Open(root));
        InfoText = "";
    });

    [RelayCommand]
    private void CloseProject()
    {
        StopAudio();
        project = null;
        ProjectPath = null;
        BackgroundImage?.Dispose();
        BackgroundImage = null;
        Revisions.Clear();
        StageLog.Clear();
        Issues.Clear();
        Timeline = null;
        ReportSummary = PackageSummary = "";
        ErrorText = InfoText = ResultText = ResultWarning = AnalysisWarning = "";
        AnalysisText = "Ainda não analisada.";
    }

    // ---- actions ----

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task Analyse() => await RunLong(async token =>
    {
        ApplySettings();
        var outcome = await service.AnalyseAsync(project!, Progress(), token);
        ShowAnalysis(outcome.Analysis);
        InfoText = outcome.Reused ? "Análise reaproveitada: o áudio, o analisador e a correção de timing não mudaram." : "Análise concluída.";
    });

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task Generate() => await RunLong(async token =>
    {
        if (!SaveFormToProject())
            return;

        var clock = Stopwatch.StartNew();
        var outcome = await service.GenerateAsync(project!, Progress(), token);
        if (IsMapperatorinator)
            LearnSpeed(clock.Elapsed, songSeconds, project!.Manifest.Settings.StrongStyle);
        Reload();
        ShowOutcome(outcome);
        InfoText = $"Revisão {outcome.Revision.Id} criada.";
    });

    /// <summary>The loudest 30 seconds only, to judge stars and style in a few minutes.</summary>
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task Preview() => await RunLong(async token =>
    {
        if (!SaveFormToProject())
            return;

        var clock = Stopwatch.StartNew();
        var outcome = await service.GenerateAsync(project!, Progress(), token, preview: true);
        LearnSpeed(clock.Elapsed, Math.Min(30, songSeconds), project!.Manifest.Settings.StrongStyle);
        Reload();
        ShowOutcome(outcome);
        InfoText = $"Prévia {outcome.Revision.Id} criada. Abra no osu! para jogar o trecho; o próximo mapa inteiro já corrige as estrelas por ela.";
    });

    [RelayCommand(CanExecute = nameof(CanGenerateMapset))]
    private async Task GenerateMapset() => await RunLong(async token =>
    {
        if (!SaveFormToProject())
            return;

        var chosen = MapsetChoices.Where(c => c.IsSelected).Select(c => new MappingService.MapsetDifficulty(c.Name, (double)(c.Stars ?? 4))).ToArray();
        var outcomes = await service.GenerateMapsetAsync(project!, chosen, Progress(), token);
        Reload();
        ResultText = string.Join(" · ", outcomes.Select(o => $"{o.Revision.Metadata.DifficultyName} {o.Revision.Generation.ActualStars:0.00}★"));
        ResultWarning = "";
        InfoText = $"Mapset com {outcomes.Count} dificuldades pronto: o próximo .osz leva todas.";
    });

    private bool CanPreview() => CanWork() && IsMapperatorinator;
    private bool CanGenerateMapset() => CanWork() && MapsetChoices.Any(c => c.IsSelected);

    [RelayCommand]
    private void TogglePackage(RevisionRow row) => Run(() =>
    {
        service.SetInPackage(project!, row.Id, !row.InPackage);
        Reload();
    });

    private void ShowOutcome(GenerationOutcome outcome)
    {
        var g = outcome.Revision.Generation;
        string engine = g.Generator switch { "mapperatorinator" => "Mapperatorinator", "b2" => "modelo de ritmo (B2)", "pulso" => "Pulso", _ => "regras (B1)" };
        string asked = g.RequestedStars is { } requested ? $" · pedido ao modelo {requested:0.##}★ para compensar" : "";
        ResultText = $"{(g.IsPreview ? "Prévia: " : "")}{outcome.Beatmap.Objects.Count} objetos · {g.ActualStars:0.00}★ (alvo {g.TargetStars:0.##}★{asked}) · {engine}";
        ResultWarning = g.TargetReached ? ""
            : g.Generator == "mapperatorinator" ? "As estrelas ficaram longe do alvo. Gere de novo: o pedido ao modelo já foi corrigido por este resultado."
            : "O alvo de estrelas não foi alcançado com saltos razoáveis; o mapa salvo é o mais próximo encontrado.";
    }

    [RelayCommand]
    private void Cancel() => running?.Cancel();

    /// <summary>Saves metadata. With a generated map, adds a revision with the same objects: nothing is analysed or generated again.</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private void SaveMetadata() => Run(() =>
    {
        var revision = service.UpdateMetadata(project!, FormMetadata());
        preferences.Creator = Creator.Trim();
        preferences.Save();
        Reload();
        InfoText = revision is null ? "Metadados salvos." : $"Metadados salvos na revisão {revision.Id}; objetos e timing não mudaram.";
    });

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Export() => Run(() =>
    {
        string package = service.Export(project!);
        InfoText = $"Pacote exportado: {package}";
    });

    /// <summary>Exports, copies the package to a temporary folder and opens the copy with its associated program (osu!lazer).</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private void OpenInOsu() => Run(() =>
    {
        string copy = service.Export(project!, Path.Combine(Path.GetTempPath(), "osu-ai-mapper", Guid.NewGuid().ToString("N")));
        Process.Start(new ProcessStartInfo(copy) { UseShellExecute = true });
        InfoText = "Uma cópia do pacote foi aberta. O osu! importa a cópia; o projeto não muda.";
    });

    [RelayCommand]
    private void OpenFolder()
    {
        if (ProjectPath is not null)
            Process.Start(new ProcessStartInfo(ProjectPath) { UseShellExecute = true });
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (project is null)
            return;

        if (player.IsPlaying)
        {
            player.Pause();
            IsPlaying = false;
            return;
        }

        Run(() =>
        {
            // The delivered audio once analysed (what the map is timed to), the original before that.
            string path = project.Manifest.Audio is { } a ? project.PathOf(a.RelativePath) : project.PathOf(project.Manifest.Original.RelativePath);
            player.Play(path);
            IsPlaying = true;
        });
    }

    [RelayCommand]
    private void StopAudio()
    {
        player.Stop();
        IsPlaying = false;
    }

    private bool CanWork() => project is not null && !IsBusy;
    private bool CanExport() => CanWork() && HasRevision;

    public void Dispose()
    {
        running?.Cancel();
        player.Dispose();
        BackgroundImage?.Dispose();
    }

    // ---- helpers ----

    private void Load(Project opened)
    {
        StopAudio();
        project = opened;
        ProjectPath = opened.Root;

        var m = opened.Manifest.Metadata;
        Title = m.Title;
        TitleUnicode = m.TitleUnicode;
        Artist = m.Artist;
        ArtistUnicode = m.ArtistUnicode;
        Creator = m.Creator;
        DifficultyName = m.DifficultyName;
        Source = m.Source;
        Tags = string.Join(' ', m.Tags);

        var s = opened.Manifest.Settings;
        TargetStars = (decimal)s.TargetStars;
        int chosen = s.RhythmModel is null ? 0 : Models.ToList().FindIndex(m => RhythmModelChoice.Same(m.Path, s.RhythmModel));
        // A project made with a model that is no longer here falls back to the best one available.
        ModelIndex = chosen >= 0 ? chosen : DefaultModelIndex();
        StyleIndex = s.Style switch { "jumps" => 1, "streams" => 2, _ => 0 };
        foreach (var tag in StyleTags)
            tag.IsSelected = s.Descriptors.Contains(tag.Value);
        MapperIndex = s.MapperId switch { null => 0, MapperatorinatorEngine.SotarksId => 1, _ => 2 };
        CustomMapperId = MapperIndex == 2 ? s.MapperId : null;
        StrongStyle = s.StrongStyle;
        Year = s.Year;
        Ar = (decimal?)s.Ar;
        Od = (decimal?)s.Od;
        Cs = (decimal?)s.Cs;
        Hp = (decimal?)s.Hp;
        Seed = s.Seed;
        TimingBpm = (decimal?)s.Timing?.Bpm;
        TimingOffsetMs = (decimal?)s.Timing?.OffsetMs;

        var original = opened.Manifest.Original;
        var facts = AudioFacts.Read(opened.PathOf(original.RelativePath));
        AudioName = original.OriginalName ?? Path.GetFileName(original.RelativePath);
        AudioInfo = $"{facts.Format} · {(facts.Duration is { } d ? $"{(int)d.TotalMinutes}:{d.Seconds:00}" : "duração desconhecida até a análise")} · {original.ByteSize / 1024.0 / 1024.0:0.0} MB";
        songSeconds = facts.Duration?.TotalSeconds ?? 0;

        ShowBackground(opened);
        AnalysisText = "Ainda não analisada.";
        AnalysisWarning = "";

        if (opened.Manifest.Analysis is { } analysis && File.Exists(opened.PathOf($"{analysis.RelativePath}/analysis.json")))
        {
            var song = SongAnalysis.Load(opened.PathOf($"{analysis.RelativePath}/analysis.json"));
            ShowAnalysis(song);
            songSeconds = song.Audio.DurationUs / 1e6;
        }
        OnPropertyChanged(nameof(EstimateText));

        ResultText = ResultWarning = ErrorText = "";
        if (opened.ActiveRevision is { } active)
        {
            var g = active.Generation;
            ResultText = $"Revisão ativa {active.Id}: {(g.IsPreview ? "prévia, " : "")}{g.ActualStars:0.00}★ (alvo {g.TargetStars:0.##}★)";
        }
        ShowReport(opened);

        RefreshRevisions();
        NotifyCommands();
    }

    private void Reload() => Load(Project.Open(project!.Root));

    private void RefreshRevisions()
    {
        Revisions.Clear();
        if (project is null)
            return;

        var package = MappingService.PackageOf(project).Select(r => r.Id).ToHashSet();
        foreach (var r in project.Manifest.Revisions.Reverse())
        {
            string kind = r.Generation.IsPreview ? "prévia" : r.Kind == RevisionKind.Generated ? r.Generation.Generator switch { "mapperatorinator" => "Mapperatorinator", "b1" => "regras", var other => other } : "metadados";
            Revisions.Add(new RevisionRow(r.Id, $"[{r.Metadata.DifficultyName}] {kind} · {r.Generation.ActualStars:0.00}★ · {r.CreatedAt.ToLocalTime():dd/MM HH:mm}",
                r.Id == project.Manifest.ActiveRevision, package.Contains(r.Id), r.Generation.IsPreview));
        }

        var packaged = MappingService.PackageOf(project);
        PackageSummary = packaged.Count switch
        {
            0 => "",
            1 => $"O .osz leva só [{packaged[0].Metadata.DifficultyName}].",
            _ => $"O .osz leva {packaged.Count} dificuldades: {string.Join(", ", packaged.Select(r => r.Metadata.DifficultyName))}.",
        };
        OnPropertyChanged(nameof(HasRevision));
    }

    /// <summary>The checks and the timeline of the active revision.</summary>
    private void ShowReport(Project opened)
    {
        Issues.Clear();
        ReportSummary = "";
        Timeline = null;
        if (opened.ActiveRevision is not { } active)
            return;

        var issues = MappingService.ReportOf(opened, active);
        foreach (var issue in issues)
        {
            string when = issue.EndMs is { } end ? $"{MapReport.Clock(issue.TimeMs)}–{MapReport.Clock(end)}" : issue.TimeMs > 0 ? MapReport.Clock(issue.TimeMs) : "";
            Issues.Add(new IssueRow(when, issue.Message, MapTimeline.BrushOf(issue.Severity)));
        }
        int serious = issues.Count(i => i.Severity != IssueSeverity.Notice);
        ReportSummary = issues.Count == 0 ? "Nada de estranho: sem trechos vazios, notas empilhadas ou saltos fora do padrão."
            : serious == 0 ? "Só observações; nada que quebre o mapa."
            : $"{serious} ponto(s) para conferir antes de jogar.";

        string analysisFile = opened.PathOf($"analysis/{active.AnalysisKey}/analysis.json");
        if (!File.Exists(analysisFile))
            return;
        var song = SongAnalysis.Load(analysisFile);
        var beatmap = OsuAi.BeatmapCore.IrJson.Deserialize<OsuAi.BeatmapCore.BeatmapIr>(File.ReadAllText(opened.PathOf(active.BeatmapIr)));
        var g = active.Generation;
        Timeline = new TimelineData(
            song.Audio.DurationUs / 1000.0,
            song.Grid.Where(p => p.Index >= 0).Select(p => (p.TimeUs / 1000.0, p.Energy)).ToArray(),
            beatmap.Objects.Select(o => o.TimeUs / 1000.0).ToArray(),
            issues,
            g.PreviewStartMs is { } from && g.PreviewEndMs is { } to ? (from, to) : null);
    }

    private void ShowAnalysis(SongAnalysis analysis)
    {
        var t = analysis.Timing;
        string source = t.IsManual ? "corrigido" : "detectado";
        AnalysisText = $"{t.Bpm:0.###} BPM ({source}) · primeiro tempo forte em {t.OffsetUs / 1000.0:0.#} ms · {analysis.Audio.DurationUs / 60_000_000}:{analysis.Audio.DurationUs / 1_000_000 % 60:00} · confiança {t.Confidence:0.00}";
        AnalysisWarning = t.Confidence < 0.5
            ? "Confiança baixa: confira o BPM ouvindo a música. Se estiver errado, informe o BPM correto em Correção de timing e analise de novo."
            : "";
    }

    private ProjectMetadata FormMetadata() => new()
    {
        Title = Title ?? "",
        TitleUnicode = TitleUnicode,
        Artist = Artist ?? "",
        ArtistUnicode = ArtistUnicode,
        Creator = Creator ?? "",
        DifficultyName = DifficultyName ?? "",
        Source = Source,
        Tags = (Tags ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
    };

    /// <summary>Writes the form into the project without creating a revision; false (with the problems shown) when invalid.</summary>
    private bool SaveFormToProject()
    {
        var problems = MetadataProblems;
        if (problems.Length > 0)
        {
            ErrorText = problems;
            return false;
        }

        service.SetMetadata(project!, FormMetadata());
        ApplySettings();
        preferences.Creator = Creator.Trim();
        preferences.Save();
        return true;
    }

    private void ApplySettings()
    {
        if (TimingOffsetMs is not null && TimingBpm is null)
            throw new ArgumentException("Para corrigir o offset, informe também o BPM.");

        service.UpdateSettings(project!, s => s with
        {
            TargetStars = (double)(TargetStars ?? 5m),
            Ar = (double?)Ar,
            Od = (double?)Od,
            Cs = (double?)Cs,
            Hp = (double?)Hp,
            Seed = (int)(Seed ?? 7319),
            Timing = TimingBpm is { } bpm ? new TimingCorrection((double)bpm, (double?)TimingOffsetMs) : null,
            RhythmModel = Models[Math.Clamp(ModelIndex, 0, Models.Count - 1)].Path,
            Style = StyleAvailable ? StyleIndex switch { 1 => "jumps", 2 => "streams", _ => null } : null,
            Descriptors = StyleTags.Where(t => t.IsSelected).Select(t => t.Value).ToArray(),
            MapperId = ChosenMapper,
            StrongStyle = StrongStyle,
            Year = (int)(Year ?? 2023),
        });
    }

    private Progress<PipelineProgress> Progress() => new(p =>
    {
        StageText = p.Message;
        HasProgressFraction = p.Fraction is not null;
        ProgressFraction = p.Fraction ?? 0;
        RemainingText = p.Remaining is { } left ? (left.TotalMinutes < 1 ? "menos de 1 min" : $"faltam ~{Math.Ceiling(left.TotalMinutes):0} min") : "";
        // Progress bars redraw often; the log keeps one line per step, not one per redraw.
        string step = p.Fraction is null ? p.Message : p.Message.Split(':')[0];
        if (StageLog.Count == 0 || !StageLog[^1].StartsWith(step, StringComparison.Ordinal))
            StageLog.Add(p.Message);
        else if (p.Fraction is not null)
            StageLog[^1] = p.Message;
    });

    private async Task RunLong(Func<CancellationToken, Task> work)
    {
        running = new CancellationTokenSource();
        IsBusy = true;
        ErrorText = InfoText = "";
        StageLog.Clear();
        var clock = Stopwatch.StartNew();

        try
        {
            await work(running.Token);
        }
        catch (OperationCanceledException)
        {
            InfoText = "Cancelado. O projeto continua como estava antes.";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ErrorText = Friendly(e);
        }
        finally
        {
            IsBusy = false;
            StageText = RemainingText = "";
            HasProgressFraction = false;
            running.Dispose();
            running = null;
            NotifyCommands();
            if (clock.Elapsed > TimeSpan.FromSeconds(45))
                LongWorkFinished?.Invoke(ErrorText.Length > 0 ? "A geração falhou." : InfoText);
        }
    }

    private void Run(Action work)
    {
        ErrorText = "";

        try
        {
            work();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ErrorText = Friendly(e);
        }

        NotifyCommands();
    }

    private void NotifyCommands()
    {
        AnalyseCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        OpenInOsuCommand.NotifyCanExecuteChanged();
        SaveMetadataCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
        GenerateMapsetCommand.NotifyCanExecuteChanged();
    }

    private static string Friendly(Exception e) => e switch
    {
        InvalidMetadataException m => string.Join(Environment.NewLine, m.Problems.Select(Translate)),
        AnalysisFailedException a => "A análise falhou. O arquivo pode estar corrompido, ser curto demais ou não ter ritmo detectável." + Environment.NewLine + a.Message,
        GenerationFailedException g => "A geração falhou." + Environment.NewLine + g.Message,
        System.ComponentModel.Win32Exception => "Não foi possível iniciar as ferramentas de análise (uv). Verifique a instalação do ambiente de desenvolvimento.",
        _ => e.Message,
    };

    /// <summary>The application layer reports problems in English; the window shows them in Portuguese.</summary>
    private static string Translate(string problem) => problem
        .Replace("Mapper (creator) is required.", "Informe o nome do mapper.")
        .Replace("Title is required.", "Informe o título da música.")
        .Replace("Artist is required.", "Informe o artista (quem canta ou toca a música).")
        .Replace("Difficulty name is required.", "Informe o nome da dificuldade.")
        .Replace("Unicode title", "O título Unicode")
        .Replace("Unicode artist", "O artista Unicode")
        .Replace("Mapper (creator)", "O nome do mapper")
        .Replace("Difficulty name", "O nome da dificuldade")
        .Replace("Title ", "O título ")
        .Replace("Artist ", "O artista ")
        .Replace("Source ", "A origem ")
        .Replace("Tag '", "A tag '")
        .Replace("Tag contains", "Uma tag contém")
        .Replace("contains a line break or another control character.", "não pode ter quebras de linha.")
        .Replace("contains a space; tags are separated by spaces in the .osu file.", "tem espaço; separe as tags com espaços, uma palavra por tag.")
        .Replace("is longer than", "passa de")
        .Replace("characters.", "caracteres.");

    private static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "projeto" : clean.Length > 80 ? clean[..80] : clean;
    }

    private static string UniqueFolder(string path)
    {
        string candidate = path;
        for (int i = 2; Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any(); i++)
            candidate = $"{path} ({i.ToString(CultureInfo.InvariantCulture)})";
        return candidate;
    }
}
