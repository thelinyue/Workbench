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

/// <summary>本地规则文件的列表投影，不直接暴露或缓存规则正文。</summary>
public sealed record LocalRuleFile(string Name, string Path, string? Version, DateTime LastWriteTime);

/// <summary>规则校验结果；错误会阻止导入或激活，警告用于提示可改进内容。</summary>
public sealed record RuleValidationIssue(string Severity, string Message)
{
    public bool IsError => string.Equals(Severity, "error", StringComparison.OrdinalIgnoreCase);
}
