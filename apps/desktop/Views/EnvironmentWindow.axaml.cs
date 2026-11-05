using Avalonia.Controls;
using OsuAi.Desktop.ViewModels;

namespace OsuAi.Desktop.Views;

public partial class EnvironmentWindow : Window
{
    public EnvironmentWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is EnvironmentViewModel vm && vm.RunCommand.CanExecute(null))
                vm.RunCommand.Execute(null);
        };
    }
}
