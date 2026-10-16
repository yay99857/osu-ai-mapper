using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OsuAi.Application;
using OsuAi.Desktop.Services;
using OsuAi.Desktop.ViewModels;
using OsuAi.Desktop.Views;

namespace OsuAi.Desktop;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ToolEnvironment tools;
            try
            {
                tools = ToolEnvironment.Discover();
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException)
            {
                // Without the tools nothing works: say why instead of closing silently.
                desktop.MainWindow = new Avalonia.Controls.Window
                {
                    Title = "osu-ai-mapper",
                    Width = 560,
                    Height = 200,
                    WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen,
                    Content = new Avalonia.Controls.TextBlock { Text = e.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(24) },
                };
                base.OnFrameworkInitializationCompleted();
                return;
            }
            var viewModel = new MainViewModel(new MappingService(tools), Preferences.Load(), new AudioPlayer(), tools);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += (_, _) => viewModel.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
