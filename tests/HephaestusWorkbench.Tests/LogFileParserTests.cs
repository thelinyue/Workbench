using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class LogFileParserTests
{
    [Fact]
    public void ValidDiagName_ExtractsDeviceAndTime()
    {
        var parser = new LogFileParser();
        var result = parser.TryParse(@"C:\logs\diag_EC661JJ042405C93_202608110952.tgz", out var item, out var error);

        Assert.True(result);
        Assert.Null(error);
        Assert.NotNull(item);
        Assert.Equal("EC661JJ042405C93", item!.DeviceId);
        Assert.Equal(new DateTime(2026, 8, 11, 9, 52, 0), item.LogTime);
    }

    [Theory]
    [InlineData("Demidiag_H43001J59E003A8E_2608111426.tgz", "H43001J59E003A8E", 14, 26)]
    [InlineData("diag_H43001J59E003A8E_2608111403.tgz", "H43001J59E003A8E", 14, 3)]
    [InlineData("vendor_export_batch_H43001J59E003A8E_2608111426.tgz", "H43001J59E003A8E", 14, 26)]
    public void SupportedPrefixesAndShortTimestamp_ExtractDeviceAndTime(string fileName, string expectedDevice, int expectedHour, int expectedMinute)
    {
        var parser = new LogFileParser();
        var result = parser.TryParse(Path.Combine(Path.GetTempPath(), fileName), out var item, out var error);

        Assert.True(result);
        Assert.Null(error);
        Assert.NotNull(item);
        Assert.Equal(expectedDevice, item!.DeviceId);
        Assert.Equal(fileName, item.FileName);
        Assert.Equal(new DateTime(2026, 8, 11, expectedHour, expectedMinute, 0), item.LogTime);
    }

    [Theory]
    [InlineData("diag_device_20260811.tgz")]
    [InlineData("customer-log.tgz")]
    [InlineData("diag_ABC_202613110952.tgz")]
    [InlineData("diag_DEVICE_2602321426.tgz")]
    [InlineData("diag_DEVICE-01_2608111426.tgz")]
    [InlineData("DEVICE_2608111426.tgz")]
    public void InvalidLogFileName_IsRejected(string fileName)
    {
        var parser = new LogFileParser();
        Assert.False(parser.TryParse(Path.Combine(Path.GetTempPath(), fileName), out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
