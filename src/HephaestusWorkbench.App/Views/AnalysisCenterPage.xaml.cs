using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HephaestusWorkbench.App.ViewModels;

namespace HephaestusWorkbench.App.Views;

public partial class AnalysisCenterPage : System.Windows.Controls.UserControl
{
    public AnalysisCenterPage() => InitializeComponent();

    /// <summary>
    /// 双击日志主体直接打开最新报告。按钮和菜单拥有各自命令，必须排除这些交互控件，
    /// 否则用户双击“打开解压目录”等按钮时会同时触发报告跳转。
    /// </summary>
    private void OnLogRowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2
            || sender is not FrameworkElement { DataContext: AnalysisLogGroupViewModel item }
            || DataContext is not AnalysisCenterViewModel viewModel)
            return;

        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null
            || FindAncestor<MenuItem>(source) is not null
            || FindAncestor<System.Windows.Controls.Primitives.TextBoxBase>(source) is not null)
            return;

        // 即使当前没有可用报告也执行命令，由 ViewModel 输出明确的非模态提示。
        viewModel.OpenRowReportCommand.Execute(item);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }
}
