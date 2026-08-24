using HephaestusWorkbench.PluginSDK;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 将已验证的正式发布交接记录映射为 Bundled Extension 锁定条目。
/// Catalog description 属于人工审核的编辑性信息，不能从 manifest 猜测，调用方必须显式提供。
/// </summary>
public static class ExtensionReleaseHandoffMapper
{
    public static BundledExtensionItem ToBundledExtension(
        ExtensionReleaseMetadataPackage package,
        string reviewedDescription)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Manifest is null)
            throw new InvalidDataException("Extension release metadata 缺少 manifest。");
        if (string.IsNullOrWhiteSpace(reviewedDescription))
            throw new InvalidDataException("生成 Bundled Extension 前必须显式提供已审核的 description。");

        var item = new BundledExtensionItem
        {
            Id = package.Manifest.Id,
            Name = package.Manifest.Name,
            Description = reviewedDescription,
            PublisherId = package.Manifest.PublisherId,
            Kind = package.Manifest.Kind,
            Asset = package.File,
            Release = package.ToRelease()
        };

        _ = BundledExtensionManifestParser.Parse(System.Text.Json.JsonSerializer.Serialize(
            new BundledExtensionDocument { SchemaVersion = 2, Extensions = [item] }));
        return item;
    }
}
