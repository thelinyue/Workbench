using HephaestusWorkbench.Core.Models;

namespace HephaestusWorkbench.Tests;

public sealed class SshConnectionTemplateTests
{
    [Fact]
    public void Parse_ValidIpAndPortTemplate_ReturnsHostAndPort()
    {
        var template = SshConnectionTemplate.Parse("""{"port":38977,"ip":"cn68-relay.ugnas.com"}""");

        Assert.Equal("cn68-relay.ugnas.com", template.Host);
        Assert.Equal(38977, template.Port);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"ip":"","port":38977}""")]
    [InlineData("""{"ip":"server","port":0}""")]
    [InlineData("not json")]
    public void Parse_InvalidTemplate_ThrowsReadableValidationError(string json)
    {
        var error = Assert.Throws<InvalidDataException>(() => SshConnectionTemplate.Parse(json));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }
}

