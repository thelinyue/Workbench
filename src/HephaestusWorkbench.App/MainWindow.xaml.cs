using System.Windows;
using HephaestusWorkbench.App.ViewModels;

namespace HephaestusWorkbench.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Escape || !viewModel.TaskPanel.IsOpen) return;
            viewModel.TaskPanel.CloseCommand.Execute(null);
            args.Handled = true;
        };
    }
}
