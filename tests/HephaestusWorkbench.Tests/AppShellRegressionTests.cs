using System.Globalization;
using System.Windows;
using HephaestusWorkbench.App;

namespace HephaestusWorkbench.Tests;

public sealed class AppShellRegressionTests
{
    [Fact]
    public void AppVersionInfo_UsesInformationalVersionWithoutBuildMetadata()
    {
        Assert.Equal("v2.0.0", AppVersionInfo.DisplayVersion);
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
}
