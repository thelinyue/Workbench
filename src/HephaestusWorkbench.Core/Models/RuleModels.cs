using System.Text.Json.Serialization;

namespace HephaestusWorkbench.Core.Models;

/// <summary>与日志分析插件兼容的规则 JSON 模型，工作台仅负责保存和校验。</summary>
public sealed class RuleSet
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
    [JsonPropertyName("files")]
    public List<RuleFile> Files { get; set; } = new();
}

/// <summary>描述一类日志文件及其按顺序执行的关键词规则。</summary>
public sealed class RuleFile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
    [JsonPropertyName("keywords")]
    public List<RuleDefinition> Keywords { get; set; } = new();
}

/// <summary>描述单条关键词或正则规则，以及命中后的严重度和上下文采集方式。</summary>
public sealed class RuleDefinition
{
    [JsonPropertyName("term")]
    public string Term { get; set; } = string.Empty;
    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;
    [JsonPropertyName("regex")]
    public bool Regex { get; set; }
    [JsonPropertyName("severity")]
    public string? Severity { get; set; }
    [JsonPropertyName("context_lines")] public int ContextLines { get; set; }
    [JsonPropertyName("context_direction")] public string ContextDirection { get; set; } = "down";
    [JsonPropertyName("search_direction")] public string SearchDirection { get; set; } = "down";
}

/// <summary>用户规则的本地管理状态，不写入分析器使用的 active.json。</summary>
public sealed class UserRuleSet
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("baseVersion")]
    public string? BaseVersion { get; set; }
    /// <summary>用户先创建的分类清单；分类可以暂时没有规则，旧文件缺失该字段时由规则记录迁移生成。</summary>
    [JsonPropertyName("categories")]
    public List<string>? Categories { get; set; }
    [JsonPropertyName("rules")]
    public List<UserRuleRecord> Rules { get; set; } = new();
}

/// <summary>一条用户规则及其审核、冲突和选择状态。</summary>
public sealed class UserRuleRecord
{
    [JsonPropertyName("localId")]
    public string LocalId { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
    [JsonPropertyName("rule")]
    public RuleDefinition Rule { get; set; } = new();
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("status")]
    public string Status { get; set; } = "draft";
    [JsonPropertyName("selected")]
    public bool Selected { get; set; }
    [JsonPropertyName("submissionId")]
    public string? SubmissionId { get; set; }
    [JsonPropertyName("conflictMessage")]
    public string? ConflictMessage { get; set; }
}

/// <summary>用户向维护者提交的规则增量，禁止携带完整 active.json。</summary>
public sealed class RuleSubmission
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("ruleSetId")]
    public string RuleSetId { get; set; } = "log-analyzer";
    [JsonPropertyName("baseVersion")]
    public string? BaseVersion { get; set; }
    [JsonPropertyName("changes")]
    public List<RuleChange> Changes { get; set; } = new();
}

/// <summary>单条用户规则提交变更。</summary>
public sealed class RuleChange
{
    [JsonPropertyName("localId")]
    public string LocalId { get; set; } = string.Empty;
    [JsonPropertyName("action")]
    public string Action { get; set; } = "add";
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;
    [JsonPropertyName("rule")]
    public RuleDefinition Rule { get; set; } = new();
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>规则同步清单。规则包通过 HTTPS 下载，并使用 Ed25519 签名校验。</summary>
public sealed class RuleCatalogEntry
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }
    [JsonPropertyName("ruleSetId")]
    public string RuleSetId { get; set; } = string.Empty;
    [JsonPropertyName("pluginId")]
    public string PluginId { get; set; } = string.Empty;
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;
    [JsonPropertyName("minimumPluginVersion")]
    public string MinimumPluginVersion { get; set; } = string.Empty;
    [JsonPropertyName("signatureAlgorithm")]
    public string SignatureAlgorithm { get; set; } = string.Empty;
    [JsonPropertyName("packageUrl")]
    public string PackageUrl { get; set; } = string.Empty;
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
    [JsonPropertyName("packageSize")]
    public long PackageSize { get; set; }
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;
    [JsonPropertyName("keyId")]
    public string KeyId { get; set; } = string.Empty;
    [JsonPropertyName("releaseNotesUrl")]
    public string? ReleaseNotesUrl { get; set; }
}

/// <summary>工作台展示的规则同步状态。</summary>
public sealed class RuleStateSnapshot
{
    [JsonPropertyName("officialVersion")]
    public string? OfficialVersion { get; set; }
    [JsonPropertyName("localRuleCount")]
    public int LocalRuleCount { get; set; }
    [JsonPropertyName("pendingRuleCount")]
    public int PendingRuleCount { get; set; }
    [JsonPropertyName("conflictRuleCount")]
    public int ConflictRuleCount { get; set; }
    [JsonPropertyName("lastCheckedAt")]
    public DateTime? LastCheckedAt { get; set; }
    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }
    [JsonPropertyName("submissionAvailable")]
    public bool SubmissionAvailable { get; set; }
    [JsonPropertyName("submissionUnavailableReason")]
    public string? SubmissionUnavailableReason { get; set; }
}

/// <summary>规则编辑器一次加载所需的只读主规则、本地规则和激活结果。</summary>
public sealed class RuleEditorState
{
    [JsonPropertyName("official")]
    public RuleSet Official { get; set; } = new();
    [JsonPropertyName("user")]
    public UserRuleSet User { get; set; } = new();
    [JsonPropertyName("active")]
    public RuleSet Active { get; set; } = new();
    [JsonPropertyName("state")]
    public RuleStateSnapshot State { get; set; } = new();
}

public sealed record RuleSyncResult(bool Updated, string? Version, string Message);

/// <summary>本地规则文件的列表投影，不直接暴露或缓存规则正文。</summary>
public sealed record LocalRuleFile(string Name, string Path, string? Version, DateTime LastWriteTime);

/// <summary>规则校验结果；错误会阻止导入或激活，警告用于提示可改进内容。</summary>
public sealed record RuleValidationIssue(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("localIds")] IReadOnlyList<string>? LocalIds = null,
    [property: JsonPropertyName("field")] string? Field = null)
{
    public bool IsError => string.Equals(Severity, "error", StringComparison.OrdinalIgnoreCase);
}
