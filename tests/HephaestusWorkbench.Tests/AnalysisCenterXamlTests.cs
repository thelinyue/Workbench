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
        Assert.Contains(buttons, element => (string?)element.Attribute("Command") == "{Binding DeleteInvalidCommand}");
        Assert.Contains(buttons, element => (string?)element.Attribute("Command") == "{Binding RefreshCommand}");
        Assert.Contains(document.Descendants(presentation + "Grid"), grid =>
            grid.Descendants(presentation + "Button").Any(button => (string?)button.Attribute("Command") == "{Binding AnalyzeAllPendingCommand}")
            && grid.Descendants(presentation + "Button").Any(button => (string?)button.Attribute("Command") == "{Binding RefreshCommand}"));

        var formats = buttons
            .Select(element => (string?)element.Attribute("ContentStringFormat"))
            .Where(value => value is not null)
            .ToArray();
        Assert.Contains("分析全部待分析（{0}）", formats);
        Assert.Contains("删除异常日志（{0}）", formats);
        Assert.Contains("{}{0} 次", formats);
    }

    [Fact]
    public void MoreMenus_KeepFluentFontOnIconOnly()
    {
        var document = LoadAnalysisCenterXaml();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var menuRoots = document.Descendants(presentation + "MenuItem")
            .Where(element => element.Elements(presentation + "MenuItem").Any())
            .ToArray();

        Assert.Equal(2, menuRoots.Length);
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

        Assert.Single(commands);
        Assert.All(commands, command => Assert.Contains("ElementName=Root", command));
        Assert.DoesNotContain(commands, command => command.Contains("AncestorType=Menu", StringComparison.Ordinal));
    }

    private static XDocument LoadAnalysisCenterXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HephaestusWorkbench.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory!.FullName, "src", "HephaestusWorkbench.App", "Views", "AnalysisCenterPage.xaml"));
    }
}
