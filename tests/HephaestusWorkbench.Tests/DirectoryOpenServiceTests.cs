using System.Diagnostics;
using HephaestusWorkbench.App;
using HephaestusWorkbench.Services;

namespace HephaestusWorkbench.Tests;

public sealed class DirectoryOpenServiceTests
{
    [Fact]
    public void OpenExtractDirectory_UsesNormalizedExistingDirectory()
    {
        var root = CreateRoot();
        try
        {
            ProcessStartInfo? started = null;
            var service = new DirectoryOpenService(new WorkbenchLogger(root), startInfo => started = startInfo);

            var result = service.OpenExtractDirectory(Path.Combine(root, ".", "Extract"));

            Assert.True(result.Succeeded);
            Assert.NotNull(started);
            Assert.Equal(Path.Combine(root, "Extract"), started.FileName);
            Assert.True(started.UseShellExecute);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OpenExtractDirectory_MissingDirectoryDoesNotStartAndWritesChineseLog()
    {
        var root = CreateRoot();
        try
        {
            var started = false;
            var service = new DirectoryOpenService(new WorkbenchLogger(root), _ => started = true);

            var result = service.OpenExtractDirectory(Path.Combine(root, "missing"));

            Assert.False(result.Succeeded);
            Assert.False(started);
            Assert.Contains("解压目录不存在或已被清理", result.ErrorMessage);
            Assert.Contains("解压目录不存在或已被清理", File.ReadAllText(Path.Combine(root, "Logs", "workbench.log")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "HephaestusWorkbenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Extract"));
        return root;
    }
}
