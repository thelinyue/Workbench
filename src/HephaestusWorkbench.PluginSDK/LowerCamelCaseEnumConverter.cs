using System.Text.Json;
using System.Text.Json.Serialization;

namespace HephaestusWorkbench.PluginSDK;

/// <summary>
/// 将协议枚举严格序列化为 lowerCamelCase 字符串，并拒绝数字或大小写不一致的输入。
/// 这样即使调用方未提供自定义 JsonSerializerOptions，也不会意外改变公开 JSON 契约。
/// </summary>
public sealed class LowerCamelCaseEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private static readonly IReadOnlyDictionary<TEnum, string> Names = Enum.GetValues<TEnum>()
        .ToDictionary(value => value, value => JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));

    private static readonly IReadOnlyDictionary<string, TEnum> Values = Names
        .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"{typeof(TEnum).Name} 必须使用字符串表示。");
        }

        var value = reader.GetString();
        if (value is null || !Values.TryGetValue(value, out var parsed))
        {
            throw new JsonException($"{value} 不是有效的 {typeof(TEnum).Name} 值。");
        }

        return parsed;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (!Names.TryGetValue(value, out var name))
        {
            throw new JsonException($"{value} 不是有效的 {typeof(TEnum).Name} 值。");
        }

        writer.WriteStringValue(name);
    }
}
