namespace HephaestusWorkbench.Tests;

public sealed class SystemDiagnosisNamingTests
{
    [Fact]
    public void UserVisiblePluginText_UsesSystemDiagnosisName()
    {
        var expectedTexts = new Dictionary<string, string[]>
        {
            ["src/HephaestusWorkbench.App/FirstRunWizard.xaml"] =
            [
                "内置系统诊断插件会登记到以下目录"
            ],
            ["src/HephaestusWorkbench.Services/ProcessPluginRunners.cs"] =
            [
                "系统诊断插件执行失败。",
                "系统诊断完成，但未找到指定输出目录中的 reports.json 或 report.html。",
                "系统诊断插件完成：",
                "现有系统诊断插件执行失败："
            ],
            ["src/HephaestusWorkbench.Services/CaseAnalysisService.cs"] =
            [
                "没有可用的系统诊断插件。"
            ],
            ["src/HephaestusWorkbench.App/ViewModels/MainViewModel.cs"] =
            [
                "没有可用的系统诊断插件"
            ],
            ["src/HephaestusWorkbench.App/App.xaml.cs"] =
            [
                "开始登记内置系统诊断插件。",
                "内置系统诊断插件登记完成。"
            ],
            ["src/HephaestusWorkbench.Services/PluginProvisioningService.cs"] =
            [
                "未找到内置系统诊断插件：",
                "现有系统诊断插件已更新到用户插件目录。",
                "现有系统诊断插件已登记到用户插件目录。"
            ]
        };

        var repositoryRoot = FindRepositoryRoot();
        foreach (var (relativePath, expected) in expectedTexts)
        {
            var content = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
            Assert.All(expected, text => Assert.Contains(text, content));
            Assert.DoesNotContain("日志分析插件", content);
            Assert.DoesNotContain("日志分析完成", content);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}