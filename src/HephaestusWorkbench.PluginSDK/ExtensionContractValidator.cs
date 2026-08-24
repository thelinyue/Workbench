namespace HephaestusWorkbench.PluginSDK;

/// <summary>表示扩展清单或协议数据违反 v2 契约，消息可直接用于中文诊断日志。</summary>
public sealed class ExtensionContractException : Exception
{
    public ExtensionContractException(string message) : base(message)
    {
    }

    public ExtensionContractException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// 集中执行扩展契约的结构与安全校验。安装、扫描和加载阶段必须复用同一组规则，避免校验口径漂移。
/// </summary>
public static class ExtensionContractValidator
{
    private static readonly HashSet<string> WorkspaceCapabilities = new(StringComparer.Ordinal)
    {
        "workspace.page"
    };

    private static readonly HashSet<string> AnalysisProcessCapabilities = new(StringComparer.Ordinal)
    {
        "analysis.engine",
        "analysis.scope.comprehensive",
        "analysis.scope.storage"
    };

    private static readonly HashSet<string> AnalysisContentCapabilities = new(StringComparer.Ordinal)
    {
        "analysis.rule-pack",
        "analysis.report-template"
    };

    private static readonly HashSet<string> MaintenanceCapabilities = new(StringComparer.Ordinal)
    {
        "maintenance.workflow-pack",
        "maintenance.command-profile"
    };

    public static void ValidateManifest(ExtensionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.SchemaVersion != 2)
            throw new ExtensionContractException("扩展清单 schemaVersion 必须为 2。");
        if (manifest.Runtime is null || manifest.Capabilities is null || manifest.Permissions is null || manifest.Dependencies is null)
            throw new ExtensionContractException("扩展清单 runtime、capabilities、permissions 和 dependencies 不能为空。");

        ValidateIdentityAndVersions(manifest);
        ValidateDependencies(manifest);
        ValidateKindRuntime(manifest);
        ValidateRuntime(manifest);
        ValidateCapabilities(manifest);
        ValidatePermissions(manifest);
    }

    private static void ValidateIdentityAndVersions(ExtensionManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Name))
            throw new ExtensionContractException("扩展 name 不能为空。");
        if (!ExtensionContractValues.IsIdentifier(manifest.Id))
            throw new ExtensionContractException("扩展 id 必须使用小写字母、数字、点号或连字符。");
        if (!ExtensionContractValues.IsIdentifier(manifest.PublisherId))
            throw new ExtensionContractException("扩展 publisherId 必须使用小写字母、数字、点号或连字符。");
        if (!ExtensionContractValues.IsSemanticVersion(manifest.Version))
            throw new ExtensionContractException("扩展 version 必须是有效的语义化版本。");
        if (!string.Equals(manifest.HostApiVersion, "1.0", StringComparison.Ordinal))
            throw new ExtensionContractException("扩展 hostApiVersion 必须为 1.0。");
        if (!ExtensionContractValues.IsSemanticVersion(manifest.MinHostVersion))
            throw new ExtensionContractException("扩展 minHostVersion 必须是有效的语义化版本。");
    }

    private static void ValidateDependencies(ExtensionManifest manifest)
    {
        var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in manifest.Dependencies)
        {
            if (dependency is null)
                throw new ExtensionContractException("扩展 dependencies 不能包含 null 元素。");
            if (!ExtensionContractValues.IsIdentifier(dependency.Id))
                throw new ExtensionContractException($"扩展依赖 id 无效：{dependency.Id}");
            if (!ExtensionContractValues.IsSemanticVersion(dependency.Version))
                throw new ExtensionContractException($"扩展依赖 {dependency.Id} 的 version 无效。");
            if (string.Equals(dependency.Id, manifest.Id, StringComparison.Ordinal))
                throw new ExtensionContractException("扩展不能依赖自身。");
            if (!dependencyIds.Add(dependency.Id))
                throw new ExtensionContractException($"扩展依赖重复：{dependency.Id}");
        }
    }

    private static void ValidateKindRuntime(ExtensionManifest manifest)
    {
        var valid = (manifest.Kind, manifest.Runtime.Kind) switch
        {
            (ExtensionKind.Workspace, ExtensionRuntimeKind.Web) => true,
            (ExtensionKind.Analysis, ExtensionRuntimeKind.Process) => true,
            (ExtensionKind.Analysis, ExtensionRuntimeKind.Content) => true,
            (ExtensionKind.Maintenance, ExtensionRuntimeKind.Content) => true,
            _ => false
        };

        if (!valid)
            throw new ExtensionContractException($"扩展 {manifest.Id} 的 kind/runtime 组合不受支持。");
    }

    private static void ValidateRuntime(ExtensionManifest manifest)
    {
        if (manifest.Runtime.Kind == ExtensionRuntimeKind.Process &&
            !string.Equals(manifest.Runtime.Protocol, AnalysisProcessProtocol.Version, StringComparison.Ordinal))
        {
            throw new ExtensionContractException($"analysis/process 扩展必须使用 {AnalysisProcessProtocol.Version} 协议。");
        }

        if (manifest.Runtime.Kind == ExtensionRuntimeKind.Content && !string.IsNullOrWhiteSpace(manifest.Runtime.Entry))
            throw new ExtensionContractException("content 运行时不能声明 entry。");

        if (manifest.Runtime.Kind is ExtensionRuntimeKind.Process or ExtensionRuntimeKind.Web)
            ValidateEntryPath(manifest);
    }

    private static void ValidateEntryPath(ExtensionManifest manifest)
    {
        try
        {
            var entry = manifest.Runtime.Entry;
            if (string.IsNullOrWhiteSpace(entry) || Path.IsPathRooted(entry))
                throw new ExtensionContractException("扩展入口必须位于扩展版本目录内。");

            var root = Path.GetFullPath(manifest.DirectoryPath);
            var resolved = Path.GetFullPath(Path.Combine(root, entry));
            var relative = Path.GetRelativePath(root, resolved);
            if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw new ExtensionContractException("扩展入口必须位于扩展版本目录内。");
            }

            RejectReparsePoints(root, relative);
        }
        catch (ExtensionContractException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new ExtensionContractException($"扩展入口路径无效：{exception.Message}", exception);
        }
    }

    private static void RejectReparsePoints(string root, string relativeEntry)
    {
        var current = root;
        if (PathExists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            throw new ExtensionContractException("扩展版本目录不能是重解析点。");

        foreach (var segment in relativeEntry.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!PathExists(current)) break;
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new ExtensionContractException($"扩展入口路径不能经过重解析点：{current}");
        }
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void ValidateCapabilities(ExtensionManifest manifest)
    {
        if (manifest.Capabilities.Count == 0)
            throw new ExtensionContractException($"扩展 {manifest.Id} 必须声明至少一个能力。");

        var allowed = (manifest.Kind, manifest.Runtime.Kind) switch
        {
            (ExtensionKind.Workspace, ExtensionRuntimeKind.Web) => WorkspaceCapabilities,
            (ExtensionKind.Analysis, ExtensionRuntimeKind.Process) => AnalysisProcessCapabilities,
            (ExtensionKind.Analysis, ExtensionRuntimeKind.Content) => AnalysisContentCapabilities,
            (ExtensionKind.Maintenance, ExtensionRuntimeKind.Content) => MaintenanceCapabilities,
            _ => throw new ExtensionContractException("扩展 kind/runtime 组合不受支持。")
        };

        foreach (var capability in manifest.Capabilities)
        {
            if (!allowed.Contains(capability))
                throw new ExtensionContractException($"扩展 {manifest.Id} 的运行时不允许声明能力 {capability}。");
        }

        var requiredCapability = (manifest.Kind, manifest.Runtime.Kind) switch
        {
            (ExtensionKind.Workspace, ExtensionRuntimeKind.Web) => "workspace.page",
            (ExtensionKind.Analysis, ExtensionRuntimeKind.Process) => "analysis.engine",
            _ => null
        };

        if (requiredCapability is not null && !manifest.Capabilities.Contains(requiredCapability, StringComparer.Ordinal))
            throw new ExtensionContractException($"扩展 {manifest.Id} 必须声明能力 {requiredCapability}。");
    }

    private static void ValidatePermissions(ExtensionManifest manifest)
    {
        if (manifest.Kind != ExtensionKind.Workspace && manifest.Permissions.Count > 0)
            throw new ExtensionContractException("只有 workspace 扩展可以声明 permissions。");
    }
}
