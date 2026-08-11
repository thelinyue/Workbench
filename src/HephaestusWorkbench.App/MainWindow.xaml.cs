using System.Windows;
using HephaestusWorkbench.App.ViewModels;

namespace HephaestusWorkbench.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
