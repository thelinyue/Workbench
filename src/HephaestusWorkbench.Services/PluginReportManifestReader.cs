using System.Text.Json;
using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>报告目录发现结果；清单存在时即使校验失败也不会再使用旧 report.html 回退。</summary>
internal sealed record PluginReportDiscoveryResult(
    bool ManifestExists,
    bool LegacyReportExists,
    IReadOnlyList<PluginReportArtifact> Reports,
    string? ErrorMessage);

/// <summary>
/// 解析并校验插件输出目录中的 reports.json。校验集中在 runner 边界，
/// 保证进入业务层的报告入口均为输出目录内真实存在的 HTML 文件。
/// </summary>
internal static class PluginReportManifestReader
{
    private const int SupportedSchemaVersion = 1;

    public static async Task<PluginReportDiscoveryResult> DiscoverAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        var outputDirectory = Path.GetFullPath(outputPath);
        var manifestPath = Path.Combine(outputDirectory, "reports.json");
        if (!File.Exists(manifestPath))
        {
            var legacyReportPath = Path.Combine(outputDirectory, "report.html");
            if (!File.Exists(legacyReportPath))
                return new PluginReportDiscoveryResult(false, false, Array.Empty<PluginReportArtifact>(), null);
            if (ContainsReparsePoint(outputDirectory, legacyReportPath))
                return new PluginReportDiscoveryResult(
                    false,
                    false,
                    Array.Empty<PluginReportArtifact>(),
                    "旧版报告入口无效：report.html 路径包含链接或特殊目录。");
            return new PluginReportDiscoveryResult(false, true, Array.Empty<PluginReportArtifact>(), null);
        }

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<ReportManifest>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }, cancellationToken);
            return Validate(outputDirectory, manifest);
        }
        catch (JsonException ex)
        {
            return Invalid($"报告清单 reports.json 不是有效 JSON：{ex.Message}");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Invalid($"报告入口路径无效：{ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Invalid($"读取报告清单 reports.json 失败：{ex.Message}");
        }
    }

    private static PluginReportDiscoveryResult Validate(string outputDirectory, ReportManifest? manifest)
    {
        if (manifest is null) return Invalid("报告清单 reports.json 内容为空。");
        if (manifest.SchemaVersion != SupportedSchemaVersion)
            return Invalid($"报告清单 schemaVersion 必须为 {SupportedSchemaVersion}。");
        if (manifest.Reports is null || manifest.Reports.Count == 0)
            return Invalid("报告清单必须至少包含一份报告。");
        if (manifest.Reports.Any(x => x is null))
            return Invalid("报告条目不能为空。");
        if (manifest.Reports.Count(x => x!.IsDefault) != 1)
            return Invalid("报告清单必须且只能指定一份默认报告。");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var artifacts = new List<PluginReportArtifact>(manifest.Reports.Count);
        var rootPrefix = outputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var item in manifest.Reports)
        {
            // 空条目已在上方统一拒绝，此处使用非空值继续字段和路径校验。
            var report = item!;
            var id = report.Id?.Trim();
            var title = report.Title?.Trim();
            var kind = report.Kind?.Trim();
            var file = report.File?.Trim();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)
                || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(file))
                return Invalid("报告清单中的 id、title、kind、file 均不能为空。");
            if (!ids.Add(id)) return Invalid($"报告清单包含重复的报告 id：{id}。");
            if (Path.IsPathRooted(file)) return Invalid($"报告入口必须使用输出目录内的相对路径：{file}。");

            var fullPath = Path.GetFullPath(Path.Combine(outputDirectory, file));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return Invalid($"报告入口超出插件输出目录：{file}。");
            if (!string.Equals(Path.GetExtension(fullPath), ".html", StringComparison.OrdinalIgnoreCase))
                return Invalid($"报告入口必须是 HTML 文件：{file}。");
            if (!File.Exists(fullPath)) return Invalid($"报告清单指定的入口文件不存在：{file}。");
            if (ContainsReparsePoint(outputDirectory, fullPath))
                return Invalid($"报告入口路径包含链接或特殊目录：{file}。");

            artifacts.Add(new PluginReportArtifact(id, title, kind, file, report.IsDefault));
        }

        return new PluginReportDiscoveryResult(true, false, artifacts, null);
    }

    /// <summary>
    /// GetFullPath 只能阻止文本形式的 .. 越界，无法识别 junction 或符号链接跳转。
    /// 因此从输出目录到报告文件逐级检查现有路径节点，只要出现 reparse point 就拒绝，
    /// 避免插件借助链接把看似位于输出目录内的入口指向任意外部文件。
    /// </summary>
    private static bool ContainsReparsePoint(string outputDirectory, string reportPath)
    {
        if (IsReparsePoint(outputDirectory)) return true;

        var relativePath = Path.GetRelativePath(outputDirectory, reportPath);
        var currentPath = outputDirectory;
        foreach (var segment in relativePath.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (IsReparsePoint(currentPath)) return true;
        }
        return false;
    }

    private static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static PluginReportDiscoveryResult Invalid(string message)
        => new(true, false, Array.Empty<PluginReportArtifact>(), $"报告清单无效：{message}");

    private sealed class ReportManifest
    {
        public int SchemaVersion { get; init; }
        public List<ReportManifestItem?>? Reports { get; init; }
    }

    private sealed class ReportManifestItem
    {
        public string? Id { get; init; }
        public string? Title { get; init; }
        public string? Kind { get; init; }
        public string? File { get; init; }
        public bool IsDefault { get; init; }
    }
}
