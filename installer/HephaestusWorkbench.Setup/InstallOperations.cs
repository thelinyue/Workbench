using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace HephaestusWorkbench.Setup;

internal sealed class SetupLogger
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "HephaestusWorkbench-Setup.log");
    private readonly object _sync = new();

    public void Info(string message) => Write("信息", message);
    public void Error(string message, Exception? exception = null) => Write("错误", exception is null ? message : $"{message}：{exception.Message}");

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        lock (_sync) File.AppendAllText(_file, line + Environment.NewLine);
    }
}

internal static class EnvironmentChecker
{
    public static void Ensure(SetupLogger logger)
    {
        if (!OperatingSystem.IsWindows() || Environment.OSVersion.Version < new Version(10, 0) || !Environment.Is64BitOperatingSystem)
            throw new PlatformNotSupportedException("当前系统不满足运行要求，需要 Windows 10/11 x64。");

        if (WebView2Installed()) return;
        var bundledInstaller = Path.Combine(AppContext.BaseDirectory, "Prerequisites", "MicrosoftEdgeWebView2RuntimeInstallerX64.exe");
        if (File.Exists(bundledInstaller))
        {
            logger.Info("未检测到 WebView2，开始执行随包提供的安装程序。");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = bundledInstaller,
                Arguments = "/silent install",
                UseShellExecute = true,
                Verb = "runas"
            }) ?? throw new InvalidOperationException("无法启动 WebView2 安装程序。");
            process.WaitForExit();
        }

        if (!WebView2Installed()) throw new InvalidOperationException("未检测到 Microsoft Edge WebView2 Runtime，请先安装后重试。");
    }

    private static bool WebView2Installed()
    {
        var locations = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\EdgeUpdate\Clients"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\EdgeUpdate\Clients"),
            (RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\Microsoft\EdgeUpdate\Clients")
        };
        foreach (var location in locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(location.Item1, location.Item2);
                using var clients = baseKey.OpenSubKey(location.Item3);
                if (clients is null) continue;
                foreach (var name in clients.GetSubKeyNames())
                {
                    using var client = clients.OpenSubKey(name);
                    var productName = client?.GetValue("name")?.ToString() ?? string.Empty;
                    var version = client?.GetValue("pv")?.ToString();
                    if (!string.IsNullOrWhiteSpace(version)
                        && (productName.Contains("WebView2", StringComparison.OrdinalIgnoreCase) || name.StartsWith("{F3017226", StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
            catch (SecurityException) { }
        }
        return false;
    }
}

internal sealed class InstallMetadata
{
    public const string CurrentVersion = "1.1.0";
    public string Product { get; set; } = "赫菲斯托斯工程工作台";
    public string Version { get; set; } = CurrentVersion;
    public DateTime InstalledAt { get; set; } = DateTime.Now;
}

internal static class InstallOperations
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\HephaestusWorkbench";

    public static Version? ReadInstalledVersion(string installDirectory)
    {
        var file = Path.Combine(installDirectory, "install.json");
        if (!File.Exists(file)) return null;
        try
        {
            var metadata = JsonSerializer.Deserialize<InstallMetadata>(File.ReadAllText(file));
            return Version.TryParse(metadata?.Version, out var version) ? version : null;
        }
        catch { return null; }
    }

    public static void WriteMetadata(string installDirectory)
    {
        var metadata = new InstallMetadata();
        File.WriteAllText(Path.Combine(installDirectory, "install.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void ReplaceProgramDirectory(string stagedDirectory, string installDirectory, SetupLogger logger)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(installDirectory))!;
        Directory.CreateDirectory(parent);
        var backupDirectory = installDirectory + ".previous";
        try
        {
            if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, recursive: true);
            if (Directory.Exists(installDirectory)) Directory.Move(installDirectory, backupDirectory);
            Directory.Move(stagedDirectory, installDirectory);
            if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, recursive: true);
        }
        catch
        {
            if (Directory.Exists(installDirectory)) Directory.Delete(installDirectory, recursive: true);
            if (Directory.Exists(backupDirectory)) Directory.Move(backupDirectory, installDirectory);
            logger.Error("替换程序目录失败，已尝试恢复旧版本。");
            throw;
        }
    }

    public static void BackupDatabase(SetupLogger logger)
    {
        var dataRoot = LoadDataRootFromBootstrap();
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            logger.Info("未找到数据目录指针，跳过升级前数据库备份。");
            return;
        }
        var database = Path.Combine(dataRoot, "Database", "workbench.db");
        if (!File.Exists(database)) return;
        var backupDirectory = Path.Combine(dataRoot, "Backups", $"upgrade-{DateTime.Now:yyyyMMddHHmmss}");
        Directory.CreateDirectory(backupDirectory);
        File.Copy(database, Path.Combine(backupDirectory, "workbench.db"), overwrite: true);
        logger.Info($"升级前数据库备份完成：{backupDirectory}");
    }

    public static string? LoadDataRootFromBootstrap()
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

    public static void CreateShortcuts(string installDirectory)
    {
        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "赫工.lnk"), installDirectory);
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "赫工.lnk");
        CreateShortcut(startMenu, installDirectory);
    }

    public static void RegisterUninstall(string installDirectory)
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
        key?.SetValue("DisplayName", "赫菲斯托斯工程工作台");
        key?.SetValue("DisplayVersion", InstallMetadata.CurrentVersion);
        key?.SetValue("Publisher", "赫菲斯托斯工程工作台");
        key?.SetValue("InstallLocation", installDirectory);
        key?.SetValue("UninstallString", $"\"{Path.Combine(installDirectory, "HephaestusWorkbench.exe")}\" --uninstall");
    }

    public static void ThrowIfWorkbenchRunning()
    {
        if (Process.GetProcessesByName("HephaestusWorkbench").Any())
            throw new InvalidOperationException("赫工正在运行，请先关闭程序后重试。");
    }

    public static void RemoveProgramFiles(string installDirectory, SetupLogger logger)
    {
        ThrowIfWorkbenchRunning();
        if (Directory.Exists(installDirectory)) Directory.Delete(installDirectory, recursive: true);
        DeleteShortcuts();
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        logger.Info("程序文件和快捷方式已删除，用户数据未处理。");
    }

    public static void RemoveDataDirectory(string dataRoot, SetupLogger logger, string? installDirectory = null)
    {
        var fullPath = Path.GetFullPath(dataRoot);
        if (string.Equals(Path.GetPathRoot(fullPath), fullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝删除磁盘根目录。");
        if (!string.IsNullOrWhiteSpace(installDirectory) && IsSameOrDescendant(fullPath, installDirectory))
            throw new InvalidOperationException("数据目录不能位于程序安装目录中。");
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
        logger.Info($"用户数据目录已删除：{fullPath}");
    }

    public static void RemoveBootstrap()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HephaestusWorkbench");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private static void CreateShortcut(string shortcutPath, string installDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new COMException("系统不支持创建快捷方式。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = Path.Combine(installDirectory, "HephaestusWorkbench.exe");
        shortcut.Arguments = string.Empty;
        shortcut.WorkingDirectory = installDirectory;
        shortcut.Description = "赫工（Hephaestus Workbench）日志分析工作台";
        shortcut.IconLocation = Path.Combine(installDirectory, "HephaestusWorkbench.exe") + ",0";
        shortcut.Save();
    }

    private static void DeleteShortcuts()
    {
        foreach (var path in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "赫工.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "赫工.lnk")
        })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static bool IsSameOrDescendant(string path, string parent)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }
}
