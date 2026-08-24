using System.Text.Json;

namespace HephaestusWorkbench.Core.Models;

/// <summary>
/// 从外部设备信息中提取的非敏感 SSH 连接模板。
/// 模板仅负责提供主机和端口，绝不携带用户名、密码、私钥口令或凭据引用，
/// 因而可以安全地由用户手动粘贴到连接表单后应用。
/// </summary>
public sealed record SshConnectionTemplate(string Host, int Port)
{
    /// <summary>
    /// 解析形如 <c>{"ip":"host.example","port":22}</c> 的连接模板。
    /// 字段 <c>ip</c> 兼容真实 IP 地址和主机名；解析失败时输出可直接展示给用户的中文错误。
    /// </summary>
    public static SshConnectionTemplate Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("请输入 SSH 连接模板 JSON。");

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("SSH 连接模板必须是 JSON 对象。");

            if (!document.RootElement.TryGetProperty("ip", out var hostElement) ||
                hostElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(hostElement.GetString()))
            {
                throw new InvalidDataException("SSH 连接模板缺少有效的 ip 字段。");
            }

            if (!document.RootElement.TryGetProperty("port", out var portElement) ||
                !portElement.TryGetInt32(out var port) || port is < 1 or > 65535)
            {
                throw new InvalidDataException("SSH 连接模板中的 port 必须在 1 到 65535 之间。");
            }

            return new SshConnectionTemplate(hostElement.GetString()!.Trim(), port);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("SSH 连接模板不是有效的 JSON。", exception);
        }
    }
}

