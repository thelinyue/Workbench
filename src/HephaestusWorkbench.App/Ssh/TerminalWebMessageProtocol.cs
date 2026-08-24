using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HephaestusWorkbench.App.Ssh;

internal enum TerminalInboundMessageType
{
    Ack,
    Input,
    Resize
}

internal sealed record TerminalInboundMessage(
    TerminalInboundMessageType Type,
    string? RequestId = null,
    long? Sequence = null,
    string? Data = null,
    int? Columns = null,
    int? Rows = null);

/// <summary>
/// 定义 Host 与内置 xterm.js 页之间唯一允许的 terminal-v1 消息协议。
/// 所有用户输入按 UTF-8 字节的 Base64 传输，Host 不解析命令；输出必须携带 sequence 并等待 JS ACK。
/// </summary>
internal static class TerminalWebMessageProtocol
{
    internal const string Version = "terminal-v1";

    internal static TerminalInboundMessage ParseInbound(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("终端消息不能为空。");

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("终端消息必须是 JSON 对象。");
            if (!TryRequiredString(root, "version", out var version) || !string.Equals(version, Version, StringComparison.Ordinal))
                throw new InvalidDataException("终端消息协议版本无效。");
            if (!TryRequiredString(root, "type", out var type))
                throw new InvalidDataException("终端消息缺少 type。");

            return type switch
            {
                "ack" => ParseAck(root),
                "input" => ParseInput(root),
                "resize" => ParseResize(root),
                _ => throw new InvalidDataException($"终端消息类型不受支持：{type}。")
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("终端消息不是有效 JSON。", exception);
        }
    }

    internal static string CreateOutput(long sequence, ReadOnlySpan<byte> data) => JsonSerializer.Serialize(new
    {
        version = Version,
        type = "output",
        sequence,
        data = Convert.ToBase64String(data)
    });

    private static TerminalInboundMessage ParseAck(JsonElement root)
    {
        if (!root.TryGetProperty("sequence", out var sequence) || !sequence.TryGetInt64(out var value) || value <= 0)
            throw new InvalidDataException("终端 ACK 缺少有效 sequence。");
        return new TerminalInboundMessage(TerminalInboundMessageType.Ack, Sequence: value);
    }

    private static TerminalInboundMessage ParseInput(JsonElement root)
    {
        var requestId = RequiredRequestId(root);
        if (!TryRequiredString(root, "data", out var data))
            throw new InvalidDataException("终端输入消息缺少 data。");
        try { _ = Convert.FromBase64String(data); }
        catch (FormatException exception) { throw new InvalidDataException("终端输入 data 不是有效 Base64。", exception); }
        return new TerminalInboundMessage(TerminalInboundMessageType.Input, requestId, Data: data);
    }

    private static TerminalInboundMessage ParseResize(JsonElement root)
    {
        var requestId = RequiredRequestId(root);
        if (!root.TryGetProperty("columns", out var columnsElement) || !columnsElement.TryGetInt32(out var columns) || columns is < 1 or > 1000 ||
            !root.TryGetProperty("rows", out var rowsElement) || !rowsElement.TryGetInt32(out var rows) || rows is < 1 or > 1000)
            throw new InvalidDataException("终端 resize 消息的行列数无效。");
        return new TerminalInboundMessage(TerminalInboundMessageType.Resize, requestId, Columns: columns, Rows: rows);
    }

    private static string RequiredRequestId(JsonElement root)
    {
        if (!TryRequiredString(root, "requestId", out var requestId) || requestId.Length > 128)
            throw new InvalidDataException("终端消息缺少有效 requestId。");
        return requestId;
    }

    private static bool TryRequiredString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        return root.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value = element.GetString()!);
    }
}
