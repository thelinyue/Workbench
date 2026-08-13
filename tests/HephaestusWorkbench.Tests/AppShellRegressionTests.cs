using System.Globalization;
using System.Text.Json;
using System.Windows;
using HephaestusWorkbench.App;
using HephaestusWorkbench.App.Views;

namespace HephaestusWorkbench.Tests;

public sealed class AppShellRegressionTests
{
    [Fact]
    public void AppVersionInfo_UsesInformationalVersionWithoutBuildMetadata()
    {
        Assert.Equal("v1.2.9", AppVersionInfo.DisplayVersion);
        Assert.Equal("v1.2.3", AppVersionInfo.ToDisplayVersion("1.2.3+build.42", new Version(9, 9, 9)));
    }

    [Fact]
    public void InverseVisibilityConverter_CollapsesInvalidBindingValues()
    {
        var converter = new InverseBooleanToVisibilityConverter();

        Assert.Equal(Visibility.Visible, converter.Convert(false, typeof(Visibility), null!, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, converter.Convert(true, typeof(Visibility), null!, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, converter.Convert(DependencyProperty.UnsetValue, typeof(Visibility), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void WebToolMessageParser_AcceptsObjectAndStringPayloads()
    {
        using var objectMessage = WebToolWindow.ParseWebMessage("{\"type\":\"getRuleState\"}");
        using var stringMessage = WebToolWindow.ParseWebMessage("\"{\\\"type\\\":\\\"getRuleState\\\"}\"");

        Assert.Equal("getRuleState", objectMessage.RootElement.GetProperty("type").GetString());
        Assert.Equal("getRuleState", stringMessage.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void WebToolMessageParser_RejectsEmptyStringPayload()
    {
        Assert.Throws<JsonException>(() => WebToolWindow.ParseWebMessage("\"\""));
    }
}
