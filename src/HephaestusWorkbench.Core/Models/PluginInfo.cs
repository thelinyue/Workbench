namespace HephaestusWorkbench.Core.Models;

/// <summary>数据库中缓存的插件发现结果。</summary>
public sealed class PluginInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Type { get; init; }
    public required string Path { get; init; }
    public required string Entry { get; init; }
    public bool Enabled { get; set; } = true;
}
