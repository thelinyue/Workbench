using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace HephaestusWorkbench.App;

/// <summary>
/// 从已安装程序自身执行卸载，避免把体积很大的安装包复制到程序目录。
/// 删除程序目录会延迟到当前进程退出后执行，用户数据默认保留。
/// </summary>
internal static class UninstallManager
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\HephaestusWorkbench";

    public static void Run()
    {
        if (!IsAdministrator())
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("无法定位当前程序，不能启动卸载权限提升。");
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--uninstall --elevated",
                UseShellExecute = true,
                Verb = "runas"
            });
            return;
        }

        var installDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (string.Equals(Path.GetPathRoot(installDirectory), installDirectory, StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show("无法确认程序目录，已取消卸载。", "Hephaestus工作台", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        var dataRoot = LoadDataRoot();
        var deleteData = System.Windows.MessageBox.Show(
            $"是否删除分析数据？\n\n{dataRoot ?? "未找到数据目录，默认保留"}\n\n选择“否”将保留日志、案例和报告。",
            "卸载 Hephaestus工作台",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;

        RemoveShortcutsAndRegistration();
        if (deleteData && !string.IsNullOrWhiteSpace(dataRoot)) DeleteDataDirectory(dataRoot, installDirectory);
        if (deleteData) RemoveBootstrap();
        ScheduleProgramDirectoryRemoval(installDirectory);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string? LoadDataRoot()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HephaestusWorkbench", "bootstrap.json");
        if (!File.Exists(file)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.TryGetProperty("DataRoot", out var value) ? value.GetString() : null;
        }
        catch { return null; }
    }

    private static void DeleteDataDirectory(string dataRoot, string installDirectory)
    {
        var fullPath = Path.GetFullPath(dataRoot);
        if (string.Equals(Path.GetPathRoot(fullPath), fullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝删除磁盘根目录。");
        var normalizedPath = Path.TrimEndingDirectorySeparator(fullPath) + Path.DirectorySeparatorChar;
        var normalizedInstall = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory)) + Path.DirectorySeparatorChar;
        if (normalizedPath.StartsWith(normalizedInstall, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("数据目录不能位于程序安装目录中。");
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }

    private static void RemoveShortcutsAndRegistration()
    {
        foreach (var path in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Hephaestus工作台.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Hephaestus工作台.lnk")
        })
        {
            if (File.Exists(path)) File.Delete(path);
        }
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
    }

    private static void RemoveBootstrap()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HephaestusWorkbench");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static void ScheduleProgramDirectoryRemoval(string installDirectory)
    {
        var script = Path.Combine(Path.GetTempPath(), $"HephaestusWorkbench-uninstall-{Guid.NewGuid():N}.cmd");
        var escaped = installDirectory.Replace("\"", "\"\"");
        File.WriteAllText(script, $"@echo off\r\ntimeout /t 2 /nobreak >nul\r\nrmdir /s /q \"{escaped}\"\r\ndel /q \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo
        {
            FileName = script,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        });
    }
}
