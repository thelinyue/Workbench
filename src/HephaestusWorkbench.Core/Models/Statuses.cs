namespace HephaestusWorkbench.Core.Models;

/// <summary>分析案例的生命周期状态。</summary>
public enum CaseStatus
{
    Created,
    Ready,
    Running,
    Completed,
    Failed
}

/// <summary>后台分析任务的生命周期状态。</summary>
public enum TaskStatus
{
    Waiting,
    Running,
    Completed,
    Failed,
    Cancelled
}
