using Avalonia.Headless.NUnit;
using OsuAi.Application;
using OsuAi.Desktop.ViewModels;
using OsuAi.Desktop.Views;

namespace OsuAi.Desktop.Tests;

public class EnvironmentWindowTests
{
    private static readonly EnvironmentCheck ok = new("uv", CheckStatus.Ok, "uv 0.13.1");
    private static readonly EnvironmentCheck slow = new("Mapperatorinator", CheckStatus.Warning, "sem GPU", "use TORCH_INDEX");
    private static readonly EnvironmentCheck missing = new("FFmpeg", CheckStatus.Error, "'ffmpeg' não foi encontrado", "sudo pacman -S ffmpeg");

    [Test]
    public void TheSummaryCountsProblemsAndWarnings()
    {
        Assert.That(EnvironmentViewModel.SummaryOf([ok]), Is.EqualTo("Tudo pronto para gerar."));
        Assert.That(EnvironmentViewModel.SummaryOf([ok, slow]), Is.EqualTo("Tudo pronto para gerar. 1 aviso."));
        Assert.That(EnvironmentViewModel.SummaryOf([missing, slow, slow]), Is.EqualTo("1 problema impede a geração. 2 avisos."));
    }

    [Test]
    public void OnlyErrorsAreListedWithTheirFixWhenAGenerationCannotStart()
    {
        string text = EnvironmentViewModel.ErrorsOf([ok, slow, missing]);

        Assert.That(text, Is.EqualTo("FFmpeg: 'ffmpeg' não foi encontrado → sudo pacman -S ffmpeg"));
    }

    [Test]
    public void RowsShowTheFixOnlyWhenSomethingIsWrong()
    {
        Assert.That(new CheckRow(ok with { Fix = "ignored" }).HasFix, Is.False);
        Assert.That(new CheckRow(slow).Fix, Is.EqualTo("use TORCH_INDEX"));
        Assert.That(new CheckRow(missing).IsError, Is.True);
    }

    [AvaloniaTest]
    public void TheWindowOpensAndListsTheChecks()
    {
        string root = Path.Combine(Path.GetTempPath(), "osu-ai-env-window", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var vm = new EnvironmentViewModel(new ToolEnvironment(root, "uv-that-does-not-exist"));
            var window = new EnvironmentWindow { DataContext = vm };
            window.Show();

            Assert.That(vm.RunCommand.ExecutionTask, Is.Not.Null, "opening the window starts a check");
            // The check awaits on this (UI) thread: run its jobs instead of blocking on the task.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!vm.RunCommand.ExecutionTask!.IsCompleted && DateTime.UtcNow < deadline)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(20);
            }
            Assert.That(vm.RunCommand.ExecutionTask.IsCompleted, Is.True, "the check finishes");
            Assert.That(vm.Checks.Any(r => r.Name == "uv" && r.IsError), Is.True);
            Assert.That(vm.Summary, Does.Contain("impede"));
            window.Close();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
