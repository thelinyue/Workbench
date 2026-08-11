using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;

namespace HephaestusWorkbench.Setup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var logger = new SetupLogger();
        try
        {
            var mode = args.FirstOrDefault()?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(mode))
            {
                var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? string.Empty;
                if (executableName.Contains("_Update", StringComparison.OrdinalIgnoreCase)) mode = "--update";
                if (executableName.Contains("_Uninstall", StringComparison.OrdinalIgnoreCase)) mode = "--uninstall";
            }
            if (mode == "--update")
            {
                RunUpdate(logger);
                return;
            }
            if (mode == "--uninstall")
            {
                RunUninstall(logger);
                return;
            }

            RunInstall(logger);
        }
        catch (Exception ex)
        {
            logger.Error("安装程序执行失败", ex);
            MessageBox.Show($"操作失败：{ex.Message}", "赫工", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void RunInstall(SetupLogger logger)
    {
        EnvironmentChecker.Ensure(logger);
        var defaultDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "HephaestusWorkbench");
        using var dialog = new InstallPathForm(defaultDirectory);
        if (dialog.ShowDialog() != DialogResult.OK) return;

        var installDirectory = dialog.InstallDirectory;
        InstallOrUpgrade(installDirectory, logger);
        MessageBox.Show($"赫菲斯托斯工程工作台已安装到：\n{installDirectory}", "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        if (MessageBox.Show("现在启动赫工吗？", "安装完成", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            StartWorkbench(installDirectory);
    }
    private static void RunUpdate(SetupLogger logger)
    {
        EnvironmentChecker.Ensure(logger);
        var installDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HephaestusWorkbench");
        InstallOrUpgrade(installDirectory, logger);
        MessageBox.Show("赫工升级完成。", "升级完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void RunUninstall(SetupLogger logger)
    {
        var installDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HephaestusWorkbench");
        var dataRoot = InstallOperations.LoadDataRootFromBootstrap();
        if (MessageBox.Show($"是否删除分析数据？\n\n{dataRoot ?? "未找到数据目录，默认保留"}\n\n默认选择“否”将保留日志、案例和报告。", "卸载赫工", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            InstallOperations.RemoveProgramFiles(installDirectory, logger);
            MessageBox.Show("程序已卸载，分析数据已保留。", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        InstallOperations.RemoveProgramFiles(installDirectory, logger);
        if (!string.IsNullOrWhiteSpace(dataRoot)) InstallOperations.RemoveDataDirectory(dataRoot, logger, installDirectory);
        InstallOperations.RemoveBootstrap();
        MessageBox.Show("程序和分析数据已卸载。", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void InstallOrUpgrade(string installDirectory, SetupLogger logger)
    {
        InstallOperations.ThrowIfWorkbenchRunning();
        var currentVersion = Version.Parse(InstallMetadata.CurrentVersion);
        var installedVersion = InstallOperations.ReadInstalledVersion(installDirectory);
        if (installedVersion is not null && installedVersion > currentVersion)
            throw new InvalidOperationException($"当前安装版本 {installedVersion} 高于安装包版本 {currentVersion}，已阻止降级。");

        if (installedVersion is not null) InstallOperations.BackupDatabase(logger);
        // 临时目录必须和安装目录位于同一盘符，否则 Directory.Move 会触发“跨卷移动”错误。
        var installParent = Path.GetDirectoryName(Path.GetFullPath(installDirectory));
        if (string.IsNullOrWhiteSpace(installParent))
            throw new InvalidOperationException("无法确定安装目录的父目录。");
        var temporaryDirectory = Path.Combine(installParent, $".HephaestusWorkbench-setup-{Guid.NewGuid():N}");
        try
        {
            ExtractPayload(temporaryDirectory);
            InstallOperations.ReplaceProgramDirectory(temporaryDirectory, installDirectory, logger);
            InstallOperations.WriteMetadata(installDirectory);
            InstallOperations.CreateShortcuts(installDirectory);
            InstallOperations.RegisterUninstall(installDirectory);
            logger.Info($"程序安装完成：{installDirectory}");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static void ExtractPayload(string targetDirectory)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.zip")
            ?? throw new InvalidOperationException("安装包内缺少程序文件。");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        var targetRoot = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName));
            if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装包包含无效文件路径。");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static void StartWorkbench(string installDirectory)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(installDirectory, "HephaestusWorkbench.exe"),
            WorkingDirectory = installDirectory,
            UseShellExecute = true
        });
    }
}
