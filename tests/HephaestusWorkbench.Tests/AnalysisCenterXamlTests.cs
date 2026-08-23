using System.Xml.Linq;

namespace HephaestusWorkbench.Tests;

public sealed class AnalysisCenterXamlTests
{
    [Fact]
    public void TopActionRow_ContainsActionsAndNoLegacyFilters()
    {
        var document = LoadAnalysisCenterXaml();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var bindings = document.Descendants()
            .Attributes()
            .Select(attribute => attribute.Value)
            .ToArray();

        Assert.DoesNotContain(bindings, value => value.Contains("Keyword", StringComparison.Ordinal));
        Assert.DoesNotContain(bindings, value => value.Contains("SelectedStatus", StringComparison.Ordinal));
        Assert.DoesNotContain(bindings, value => value.Contains("SelectedPlugin", StringComparison.Ordinal));
        Assert.DoesNotContain(bindings, value => value.Contains("StartDate", StringComparison.Ordinal));
        Assert.DoesNotContain(bindings, value => value.Contains("EndDate", StringComparison.Ordinal));

        var buttons = document.Descendants(presentation + "Button").ToArray();
        Assert.Contains(buttons, element => (string?)element.Attribute("Command") == "{Binding AnalyzeAllPendingCommand}");
        Assert.Contains(buttons, element => (string?)element.Attribute("Command") == "{Binding RefreshCommand}");
        Assert.Contains(buttons, element =>
            (string?)element.Attribute("Command") == "{Binding DataContext.AnalyzeSingleCommand, ElementName=Root}"
            && (string?)element.Attribute("Content") == "{Binding SingleAnalysisText}"
            && (string?)element.Attribute("ToolTip") == "开始分析或重新分析该日志");
        var menuItems = document.Descendants(presentation + "MenuItem").ToArray();
        Assert.Contains(menuItems, element => ((string?)element.Attribute("Command"))?.Contains("DeleteInvalidCommand", StringComparison.Ordinal) == true);
        Assert.Contains(menuItems, element => ((string?)element.Attribute("Command"))?.Contains("CleanupExpiredCommand", StringComparison.Ordinal) == true);
        Assert.Contains(menuItems, element => (string?)element.Attribute("HeaderStringFormat") == "删除异常日志（{0}）");
        Assert.Contains(document.Descendants(presentation + "Expander"), element => (string?)element.Attribute("IsExpanded") == "False");
        Assert.Contains(buttons, element => (string?)element.Attribute("ContentStringFormat") == "分析全部待分析（{0}）");
        Assert.Contains(document.Descendants(presentation + "Grid"), grid =>
            grid.Descendants(presentation + "Button").Any(button => (string?)button.Attribute("Command") == "{Binding AnalyzeAllPendingCommand}")
            && grid.Descendants(presentation + "Button").Any(button => (string?)button.Attribute("Command") == "{Binding RefreshCommand}"));

        var formats = buttons
            .Select(element => (string?)element.Attribute("ContentStringFormat"))
            .Where(value => value is not null)
            .ToArray();
        Assert.Contains("分析全部待分析（{0}）", formats);
        Assert.DoesNotContain("历史（{0}）", formats);
        Assert.DoesNotContain("ToggleHistoryCommand", document.ToString());
        Assert.DoesNotContain("IsHistoryExpanded", document.ToString());
        Assert.DoesNotContain("历次分析记录", document.ToString());
    }

    [Fact]
    public void LatestAttemptReports_AreVisibleAndClickableFromEachAnalysisRow()
    {
        var document = LoadAnalysisCenterXaml();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var reportLists = document.Descendants(presentation + "ItemsControl")
            .Where(element => GetAttributeValue(element, "ItemsSource") == "{Binding CurrentAttempt.Reports}")
            .ToArray();

        var reportList = Assert.Single(reportLists);
        Assert.Contains(reportList.Descendants(presentation + "Button"), button =>
            GetAttributeValue(button, "Content") == "{Binding Title}"
            && GetAttributeValue(button, "Command") == "{Binding DataContext.OpenReportCommand, ElementName=Root}"
            && GetAttributeValue(button, "CommandParameter") == "{Binding}");
    }

    [Fact]
    public void MoreMenus_KeepFluentFontOnIconOnly()
    {
        var document = LoadAnalysisCenterXaml();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var menuRoots = document.Descendants(presentation + "MenuItem")
            .Where(element => element.Elements(presentation + "MenuItem").Any()
                && element.Element(presentation + "MenuItem.Header")?.Element(presentation + "TextBlock") is not null)
            .ToArray();

        Assert.Single(menuRoots);
        foreach (var menuRoot in menuRoots)
        {
            Assert.Null(menuRoot.Attribute("FontFamily"));
            var icon = Assert.Single(menuRoot.Element(presentation + "MenuItem.Header")!.Elements(presentation + "TextBlock"));
            Assert.Equal("Segoe Fluent Icons", (string?)icon.Attribute("FontFamily"));
            Assert.All(menuRoot.Elements(presentation + "MenuItem"), item => Assert.Null(item.Attribute("FontFamily")));
        }

        var menuStyle = document.Descendants(presentation + "Style")
            .Single(element => element.Attributes().Any(attribute => attribute.Name.LocalName == "Key" && attribute.Value == "MoreMenuItemStyle"));
        Assert.Contains(menuStyle.Elements(presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "FontFamily" && (string?)setter.Attribute("Value") == "Segoe UI");
    }

    [Fact]
    public void DeleteMenuItems_BindCommandsToAnalysisCenterViewModel()
    {
        var document = LoadAnalysisCenterXaml();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var commands = document.Descendants(presentation + "MenuItem")
            .Select(element => (string?)element.Attribute("Command"))
            .Where(value => value is not null && value.Contains("Delete", StringComparison.Ordinal))
            .Select(value => value!)
            .ToArray();

        Assert.Equal(2, commands.Length);
        Assert.All(commands, command => Assert.Contains("ElementName=Root", command));
        Assert.DoesNotContain(commands, command => command.Contains("AncestorType=Menu", StringComparison.Ordinal));
    }

    [Fact]
    public void RowActionMenus_BindToAnalysisCenterRoot()
    {
        var document = LoadAnalysisCenterXaml();
        var text = document.ToString();

        Assert.DoesNotContain("AncestorType=Menu", text);
        Assert.Contains("DataContext.AnalyzeSingleCommand, ElementName=Root", text);
        Assert.Contains("DataContext.OpenExtractDirectoryCommand, ElementName=Root", text);
        Assert.Contains("DataContext.OpenReportFolderCommand, ElementName=Root", text);
    }

    [Fact]
    public void ReportTabs_SuppressMouseSelectionFrame_AndRemainKeyboardFocusable()
    {
        var document = LoadAnalysisCenterXaml();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var styles = document.Descendants(presentation + "Style");
        var tabButtonStyle = styles.Single(element => GetAttributeValue(element, "Key") == "ReportTabButtonStyle");
        var focusVisualStyle = styles.Single(element => GetAttributeValue(element, "Key") == "ReportTabKeyboardFocusVisualStyle");
        var tabButtons = document.Descendants(presentation + "Button")
            .Where(element => GetAttributeValue(element, "AutomationProperties.Name") is "打开报告标签" or "关闭报告标签")
            .ToArray();

        Assert.Equal(2, tabButtons.Length);
        Assert.All(tabButtons, button =>
        {
            Assert.Equal("{StaticResource ReportTabButtonStyle}", (string?)button.Attribute("Style"));
            Assert.Equal("True", (string?)button.Attribute("IsTabStop"));
        });
        Assert.Contains(tabButtons, button => GetAttributeValue(button, "Command")?.Contains("OpenTabCommand", StringComparison.Ordinal) == true);
        Assert.Contains(tabButtons, button => GetAttributeValue(button, "Command")?.Contains("CloseTabCommand", StringComparison.Ordinal) == true);
        Assert.Contains(tabButtonStyle.Elements(presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "FocusVisualStyle"
            && (string?)setter.Attribute("Value") == "{StaticResource ReportTabKeyboardFocusVisualStyle}");
        Assert.DoesNotContain(tabButtonStyle.Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsKeyboardFocused");
        Assert.NotNull(focusVisualStyle.Descendants(presentation + "AdornedElementPlaceholder").SingleOrDefault());
        Assert.DoesNotContain(document.Descendants(presentation + "DataTrigger"), trigger =>
            (string?)trigger.Attribute("Binding") == "{Binding IsActive}");
    }

    private static string? GetAttributeValue(XElement element, string localName)
        => element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    private static XDocument LoadAnalysisCenterXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory!.FullName, "src", "HephaestusWorkbench.App", "Views", "AnalysisCenterPage.xaml"));
    }
}
