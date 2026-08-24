using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Data;

namespace HephaestusWorkbench.Services;

internal static class FileUtilities
{
    public static string RemoveAllExtensions(string fileName)
    {
        var current = fileName;
        while (!string.IsNullOrEmpty(Path.GetExtension(current))) current = Path.GetFileNameWithoutExtension(current);
        return current;
    }

    /// <summary>返回解压目录下的统一报告目录，不再使用独立案例报告目录。</summary>
    public static string GetReportDirectory(string extractPath) => Path.Combine(Path.GetFullPath(extractPath), "Report");

    /// <summary>
    /// 删除案例关联的原始数据。路径来自数据库，删除前必须验证它们仍然符合“源文件同目录下的同名解压目录”约定，
    /// 避免异常数据导致递归删除监控根目录或其他用户目录。
    /// </summary>
    public static void DeleteCaseArtifacts(AnalysisCase item, DataPaths paths, bool deleteReport)
    {
        var artifacts = ValidateCaseArtifacts(item, paths);
        if (File.Exists(artifacts.SourcePath)) File.Delete(artifacts.SourcePath);
        DeleteDirectoryIfExists(artifacts.ExtractPath);

        if (deleteReport)
        {
            DeleteDirectoryIfExists(artifacts.CaseDirectory);
        }
    }

    /// <summary>
    /// 在执行任何删除前验证案例关联的全部路径。生命周期批量删除会先验证所有案例，
    /// 避免某个异常记录导致操作已经删除部分数据后才失败。
    /// </summary>
    public static ValidatedCaseArtifacts ValidateCaseArtifacts(AnalysisCase item, DataPaths paths)
    {
        var caseDirectory = Path.GetFullPath(paths.GetCaseDirectory(item.Id));
        if (!IsStrictChildPath(caseDirectory, paths.CasesDirectory))
            throw new InvalidOperationException("案例目录路径不安全，已拒绝删除。");

        var sourcePath = ValidateArtifactPath(item.SourcePath, "源文件");
        var extractPath = ValidateArtifactPath(item.ExtractPath, "解压目录");
        var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
        var expectedExtractPath = Path.Combine(sourceDirectory, RemoveAllExtensions(Path.GetFileName(sourcePath)));
        if (!string.Equals(extractPath, expectedExtractPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("解压目录不是原始日志同名目录，已拒绝删除。");

        EnsureNotReparsePoint(sourcePath, "源文件");
        EnsureNotReparsePoint(extractPath, "解压目录");
        EnsureNotReparsePoint(caseDirectory, "案例目录");
        return new ValidatedCaseArtifacts(sourcePath, extractPath, caseDirectory);
    }

    private static string ValidateArtifactPath(string path, string description)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"{description}路径不是绝对路径，已拒绝删除。");

        var fullPath = Path.GetFullPath(path);
        if (string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)))
            throw new InvalidOperationException($"{description}路径不安全，已拒绝删除。");
        return fullPath;
    }

    private static bool IsStrictChildPath(string path, string parent)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(path));
        return relative is not "." and not ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static void EnsureNotReparsePoint(string path, string description)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"{description}是链接或特殊目录，已拒绝删除。");
    }

    public static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// 统计目录大小时允许单个文件暂时不可读。日志目录、解压目录可能正在被分析器或其他进程写入，
    /// 这类短暂共享冲突不应阻断分析中心其余数据的加载。
    /// </summary>
    public static long GetDirectorySize(string path, Action<string, Exception>? onReadFailure = null)
    {
        if (!Directory.Exists(path)) return 0;
        return SumFileSizes(EnumerateFilesSafely(path, onReadFailure), onReadFailure);
    }

    public static long GetDirectorySizeExcluding(
        string path,
        IReadOnlyList<string> excludedDirectories,
        Action<string, Exception>? onReadFailure = null)
    {
        if (!Directory.Exists(path)) return 0;
        var files = EnumerateFilesSafely(path, onReadFailure).Where(file => !excludedDirectories.Any(excluded =>
        {
            var normalized = Path.GetFullPath(excluded).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return file.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
        }));
        return SumFileSizes(files, onReadFailure);
    }

    public static long GetFileSize(string path, Action<string, Exception>? onReadFailure = null)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            // 通过实际只读句柄读取长度，能够把文件被其他进程独占的情况纳入统一容错路径。
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onReadFailure?.Invoke(path, ex);
            return 0;
        }
    }

    private static IEnumerable<string> EnumerateFilesSafely(string path, Action<string, Exception>? onReadFailure)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onReadFailure?.Invoke(path, ex);
            return Array.Empty<string>();
        }
    }

    private static long SumFileSizes(IEnumerable<string> files, Action<string, Exception>? onReadFailure)
    {
        long total = 0;
        try
        {
            foreach (var file in files) total += GetFileSize(file, onReadFailure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onReadFailure?.Invoke("目录遍历", ex);
        }
        return total;
    }
}

internal sealed record ValidatedCaseArtifacts(string SourcePath, string ExtractPath, string CaseDirectory);
