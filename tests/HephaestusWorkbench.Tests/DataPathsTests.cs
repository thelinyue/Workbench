using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Tests;

public sealed class DataPathsTests
{
    [Fact]
    public void CasePaths_AreSeparatedByPurpose()
    {
        var paths = new DataPaths(Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N")));

        Assert.EndsWith(Path.Combine("Database", "workbench.db"), paths.DatabaseFile);
        Assert.EndsWith(Path.Combine("Cases", "case-1", "Source"), paths.GetCaseSourceDirectory("case-1"));
        Assert.EndsWith(Path.Combine("Cases", "case-1", "Extract"), paths.GetCaseExtractDirectory("case-1"));
        Assert.EndsWith(Path.Combine("Extract", "Report"), paths.GetReportDirectory(Path.Combine(paths.Root, "Extract")));
        Assert.EndsWith("Inbox", paths.InboxDirectory);
        Assert.EndsWith("Config", paths.ConfigDirectory);
        Assert.EndsWith("Logs", paths.LogsDirectory);
    }
}
