using System.Xml.Linq;

namespace HephaestusWorkbench.Tests;

/// <summary>锁定扩展中心 v2 的固定信息架构，防止旧插件商城和默认引擎选择重新进入主流程。</summary>
public sealed class ExtensionCenterPageTests
{
    [Fact]
    public void Page_UsesThreeFixedTabsAndTypeFilterWithoutLegacyPluginActions()
    {
        var document = LoadXaml();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var text = document.ToString(SaveOptions.DisableFormatting);
        var buttons = document.Descendants(presentation + "Button").ToArray();

        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "发现");
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "已安装");
        Assert.Contains(buttons, button => (string?)button.Attribute("Content") == "更新");
        Assert.Contains("TypeFilters", text, StringComparison.Ordinal);
        Assert.Contains("SearchText", text, StringComparison.Ordinal);
        Assert.Contains("OpenCommand", text, StringComparison.Ordinal);
        Assert.Contains("InstallCommand", text, StringComparison.Ordinal);
        Assert.Contains("ToggleEnabledCommand", text, StringComparison.Ordinal);
        Assert.DoesNotContain("设为默认", text, StringComparison.Ordinal);
        Assert.DoesNotContain("默认插件", text, StringComparison.Ordinal);
        Assert.DoesNotContain("规则与应用管理", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GitHubDownloadMirrorTemplate", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_ProvidesVisibleKeyboardAccessiblePrimaryControls()
    {
        var document = LoadXaml();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var controls = document.Descendants()
            .Where(element => element.Name == presentation + "Button" ||
                              element.Name == presentation + "TextBox" ||
                              element.Name == presentation + "ComboBox" ||
                              element.Name == presentation + "ListBox")
            .ToArray();

        Assert.All(controls, control =>
            Assert.False(string.IsNullOrWhiteSpace(control.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "AutomationProperties.Name")?.Value)));
        Assert.DoesNotContain(document.Descendants(), element =>
            element.Name.LocalName.Contains("Mouse", StringComparison.OrdinalIgnoreCase));
    }

    private static XDocument LoadXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(
            directory!.FullName,
            "src",
            "HephaestusWorkbench.App",
            "Views",
            "ExtensionCenterPage.xaml"));
    }
}
