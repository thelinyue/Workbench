using HephaestusWorkbench.App.ViewModels;
using HephaestusWorkbench.Core.Models;
using AnalysisTaskStatus = HephaestusWorkbench.Core.Models.TaskStatus;

namespace HephaestusWorkbench.Tests;

public sealed class MultiReportViewModelTests
{
    [Fact]
    public void AnalysisAttempt_KeepsAllReportsAndSelectsDefaultReport()
    {
        var now = DateTime.Now;
        var analysisCase = new AnalysisCase
        {
            Id = "case-1", DisplayName = "案例一", OriginalName = "diag.tgz", DeviceId = "device", LogTime = now,
            Status = CaseStatus.Completed, SourcePath = "source.tgz", ExtractPath = "extract", CreateTime = now, UpdateTime = now
        };
        var task = new AnalysisTask { Id = "task-1", CaseId = analysisCase.Id, PluginId = "plugin", Status = AnalysisTaskStatus.Completed };
        var logReport = Summary("log", "综合日志分析报告", false);
        var storageReport = Summary("storage", "存储健康诊断报告", true);

        var attempt = new AnalysisAttemptViewModel(analysisCase, task, new[] { logReport, storageReport });

        Assert.Equal(2, attempt.Reports.Count);
        Assert.Equal("storage", attempt.Report?.Id);
    }

    [Fact]
    public void PluginExecutionResult_PreservesLegacyFourParameterBinaryContract()
    {
        var resultType = typeof(HephaestusWorkbench.PluginSDK.PluginExecutionResult);
        var legacyConstructor = resultType.GetConstructor(new[]
        {
            typeof(int), typeof(string), typeof(string), typeof(bool)
        });
        var legacyDeconstruct = resultType.GetMethod(
            "Deconstruct",
            new[]
            {
                typeof(int).MakeByRefType(), typeof(string).MakeByRefType(),
                typeof(string).MakeByRefType(), typeof(bool).MakeByRefType()
            });

        Assert.NotNull(legacyConstructor);
        Assert.NotNull(legacyDeconstruct);
    }

    [Fact]
    public void ReportTabTitle_ContainsCaseAndReportNames()
    {
        var tab = new ReportTabViewModel(Summary("storage", "存储健康诊断报告", true));

        Assert.Equal("案例一 · 存储健康诊断报告", tab.Title);
    }

    private static ReportSummary Summary(string id, string title, bool isDefault) => new()
    {
        Id = id,
        CaseId = "case-1",
        CaseName = "案例一",
        DeviceId = "device",
        Path = Path.GetTempPath(),
        ExtractPath = Path.GetTempPath(),
        ReportKey = id,
        Title = title,
        Kind = id,
        EntryFile = $"{id}.html",
        IsDefault = isDefault,
        PluginName = "插件",
        CreateTime = DateTime.Now,
        IsAvailable = true
    };
}
