using System.Security.Cryptography;
using System.Text.Json;
using HephaestusWorkbench.Core.Models;
using NSec.Cryptography;

namespace HephaestusWorkbench.Services;

/// <summary>
/// 验证规则包的完整性和发布者签名。客户端只保存公钥，不保存任何能够生成签名的私密材料。
/// </summary>
public interface IRulePackageVerifier
{
    RuleSet VerifyAndRead(ReadOnlySpan<byte> packageBytes, RuleCatalogEntry catalog);
}

/// <summary>
/// 使用 Ed25519 验证规则包。签名输入固定为“版本元数据 + 规则包内容”，避免只校验内容而遗漏清单篡改。
/// </summary>
public sealed class Ed25519RulePackageVerifier : IRulePackageVerifier
{
    public const string Algorithm = "Ed25519";
    public const string DefaultKeyId = "official-2026";

    // 这是 RFC 8032 测试向量中的公钥，发布前应替换为正式 CI 密钥对应的公钥。
    private const string DefaultPublicKey = "11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=";
    private readonly RuleSetService _rules;
    private readonly byte[] _publicKey;

    public Ed25519RulePackageVerifier(RuleSetService rules, string? publicKeyBase64 = null)
    {
        _rules = rules;
        _publicKey = DecodeKey(publicKeyBase64 ?? DefaultPublicKey);
    }

    public RuleSet VerifyAndRead(ReadOnlySpan<byte> packageBytes, RuleCatalogEntry catalog)
    {
        if (!string.Equals(catalog.SignatureAlgorithm, Algorithm, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("规则包签名算法不受支持。");
        if (string.IsNullOrWhiteSpace(catalog.KeyId))
            throw new InvalidDataException("规则包缺少签名密钥标识。");

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(catalog.Signature ?? string.Empty);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("规则包签名不是有效的 Base64。", ex);
        }

        if (signature.Length != 64)
            throw new InvalidDataException("规则包签名长度无效。");

        var signedBytes = BuildSignedBytes(packageBytes, catalog);
        try
        {
            var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, _publicKey, KeyBlobFormat.RawPublicKey);
            if (!SignatureAlgorithm.Ed25519.Verify(publicKey, signedBytes, signature))
                throw new InvalidDataException("规则包验签失败，内容或发布元数据可能已被篡改。");
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("规则包公钥或签名格式无效。", ex);
        }

        RuleSet rules;
        try
        {
            rules = JsonSerializer.Deserialize<RuleSet>(packageBytes)
                ?? throw new InvalidDataException("签名规则包内容为空。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("签名规则包不是有效的规则目录 JSON。", ex);
        }

        if (!string.Equals(rules.Version, catalog.Version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"规则版本不一致：清单为 {catalog.Version}，内容为 {rules.Version}。");
        var issue = _rules.Validate(rules).FirstOrDefault(x => x.IsError);
        if (issue is not null)
            throw new InvalidDataException($"规则包校验失败：{issue.Message}");
        return rules;
    }

    /// <summary>
    /// 固定签名编码，字段顺序和分隔符属于发布协议的一部分。
    /// </summary>
    public static byte[] BuildSignedBytes(ReadOnlySpan<byte> packageBytes, RuleCatalogEntry catalog)
    {
        using var metadata = new MemoryStream();
        using (var writer = new StreamWriter(metadata, new System.Text.UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write($"{catalog.RuleSetId}\n{catalog.Version}\n{catalog.MinimumPluginVersion}\n{catalog.SchemaVersion}\n");
            writer.Flush();
        }
        metadata.Write(packageBytes);
        return metadata.ToArray();
    }

    private static byte[] DecodeKey(string value)
    {
        try
        {
            var key = Convert.FromBase64String(value);
            if (key.Length != 32) throw new InvalidDataException("Ed25519 公钥必须为 32 字节。");
            return key;
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Ed25519 公钥不是有效的 Base64。", ex);
        }
    }
}
