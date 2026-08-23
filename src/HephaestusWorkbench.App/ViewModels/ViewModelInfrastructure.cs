using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace HephaestusWorkbench.App.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class DelegateCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public DelegateCommand(Action execute) : this(_ => execute()) { }
    public DelegateCommand(Action execute, Func<bool> canExecute) : this(_ => execute(), _ => canExecute()) { }
    public DelegateCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record NavigationItem(string Key, string Title, string Icon);

/// <summary>Shell 左侧导航的固定分组，扩展不能向该集合动态注册页面。</summary>
public sealed record NavigationSection(string Title, IReadOnlyList<NavigationItem> Items);

/// <summary>
/// 创建 v2.0.0 固定导航。导航由 Host 编译期定义，manifest 不参与位置、顺序或默认页面决策。
/// </summary>
public static class ShellNavigation
{
    public static IReadOnlyList<NavigationSection> CreateFixed() =>
    [
        new("工作",
        [
            new NavigationItem("analysis", "分析中心", "\uE896"),
            new NavigationItem("ssh", "SSH 终端", "\uE756")
        ]),
        new("扩展",
        [
            new NavigationItem("extensions", "扩展中心", "\uECAA")
        ]),
        new("系统",
        [
            new NavigationItem("settings", "设置", "\uE713")
        ])
    ];
}

public static class ViewModelFormatting
{
    public static string Size(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024 / 1024:N2} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024:N2} MB";
        if (bytes >= 1024) return $"{bytes / 1024d:N2} KB";
        return $"{bytes:N0} B";
    }
}
