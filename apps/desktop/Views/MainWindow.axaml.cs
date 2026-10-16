using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OsuAi.Desktop.ViewModels;

namespace OsuAi.Desktop.Views;

/// <summary>Only what needs the window: file and folder pickers and drag and drop. The rest is in the view model.</summary>
public partial class MainWindow : Window
{
    private static readonly string[] audio_patterns = ["*.mp3", "*.ogg", "*.wav", "*.flac", "*.m4a"];
    private static readonly string[] image_patterns = ["*.jpg", "*.jpeg", "*.png"];

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.LongWorkFinished += OnLongWorkFinished;
        };
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    /// <summary>
    /// A generation takes tens of minutes and the user works elsewhere meanwhile: when it ends, the
    /// taskbar button blinks until the window is looked at, with the system's notification sound.
    /// </summary>
    private void OnLongWorkFinished(string message)
    {
        if (IsActive || !OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero)
            return;
        var flash = new TaskbarFlash.FlashInfo
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<TaskbarFlash.FlashInfo>(),
            Window = handle,
            Flags = TaskbarFlash.FlashTray | TaskbarFlash.FlashUntilForeground,
        };
        TaskbarFlash.FlashWindowEx(ref flash);
        TaskbarFlash.MessageBeep(TaskbarFlash.BeepInformation);
    }

    private async void OnManageModels(object? sender, RoutedEventArgs e)
    {
        var manager = ViewModel.CreateModelsManager();
        await new ModelsWindow { DataContext = manager }.ShowDialog(this);
        if (manager.Changed)
            ViewModel.ReloadModels();
    }

    private async void OnNewProject(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Escolha a música",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Áudio") { Patterns = audio_patterns }],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } audio)
            return;

        await CreateProjectFor(audio);
    }

    private async void OnChooseBackground(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Escolha a imagem de fundo",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Imagem (JPG ou PNG)") { Patterns = image_patterns }],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } image)
            ViewModel.SetBackground(image);
    }

    private async void OnOpenProject(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Pasta do projeto", AllowMultiple = false });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } root)
            ViewModel.OpenProject(root);
    }

    private async Task CreateProjectFor(string audio)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Onde guardar o projeto (uma pasta nova será criada dentro dela)",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } parent)
            ViewModel.CreateProject(audio, parent);
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault();

        if (file?.TryGetLocalPath() is not { } path)
            return;

        if (Directory.Exists(path) && File.Exists(Path.Combine(path, "project.json")))
            ViewModel.OpenProject(path);
        else if (ViewModel.HasProject && MainViewModel.IsBackgroundFile(path))
            ViewModel.SetBackground(path);
        else if (MainViewModel.IsBackgroundFile(path))
            return;
        else if (File.Exists(path))
            await CreateProjectFor(path);
    }
}

/// <summary>The Win32 calls behind the end-of-generation notice.</summary>
internal static class TaskbarFlash
{
    public const uint FlashTray = 0x2;
    public const uint FlashUntilForeground = 0xC;
    public const uint BeepInformation = 0x40;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool FlashWindowEx(ref FlashInfo info);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool MessageBeep(uint type);
}
