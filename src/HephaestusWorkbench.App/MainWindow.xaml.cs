using System.Windows;
using HephaestusWorkbench.App.ViewModels;

namespace HephaestusWorkbench.App;

/// <summary>Workbench v2 固定 Shell 窗口。</summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
