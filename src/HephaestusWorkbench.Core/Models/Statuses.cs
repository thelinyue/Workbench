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


/// <summary>单次日志分析任务的分析范围。</summary>
public enum AnalysisScope
{
    /// <summary>执行当前日志分析插件已有的标准综合分析。</summary>
    Comprehensive,

    /// <summary>执行日志分析插件后续提供的存储专项分析。</summary>
    Storage
}
