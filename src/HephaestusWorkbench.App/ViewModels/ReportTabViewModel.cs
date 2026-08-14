using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>保存单个报告 Tab 的展示状态；WebView2 生命周期由对应视图负责。</summary>
public sealed class ReportTabViewModel : ViewModelBase
{
    private double _scrollPosition;
    private string _loadError = string.Empty;
    private bool _isActive;

    public ReportTabViewModel(ReportSummary report, string? sessionId = null)
    {
        Report = report;
        SessionId = sessionId ?? Guid.NewGuid().ToString("N");
        LastOpenTime = DateTime.Now;
    }

    public event EventHandler? ScrollPositionChanged;
    public event EventHandler? DisposeRequested;
    public ReportSummary Report { get; }
    public string SessionId { get; }
    public string Title => Report.CaseName;
    public string ReportFile => Report.ReportFile;
    public DateTime LastOpenTime { get; set; }
    public bool IsAvailable => Report.IsAvailable;
    public bool IsActive { get => _isActive; internal set => SetProperty(ref _isActive, value); }
    public string LoadError { get => _loadError; set => SetProperty(ref _loadError, value); }
    public double ScrollPosition
    {
        get => _scrollPosition;
        set
        {
            var safeValue = double.IsFinite(value) && value >= 0 ? value : 0;
            if (!SetProperty(ref _scrollPosition, safeValue)) return;
            ScrollPositionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RequestDispose() => DisposeRequested?.Invoke(this, EventArgs.Empty);
}
