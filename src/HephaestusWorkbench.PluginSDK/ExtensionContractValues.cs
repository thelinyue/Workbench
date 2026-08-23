namespace HephaestusWorkbench.PluginSDK;

/// <summary>
/// 扩展协议跨文档复用的基础值规则。Registry、Installer 和 Host 不应各自实现不同的 ID、版本或哈希解释。
/// </summary>
internal static class ExtensionContractValues
{
    public static bool IsIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var segments = value.Split('.', '-');
        return segments.All(segment => segment.Length > 0 &&
            segment.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9'));
    }

    public static bool IsSemanticVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var buildSplit = value.Split('+');
        if (buildSplit.Length > 2 || buildSplit.Any(string.IsNullOrEmpty)) return false;
        if (buildSplit.Length == 2 && !AreValidIdentifiers(buildSplit[1], rejectNumericLeadingZero: false)) return false;

        var versionAndPrerelease = buildSplit[0].Split('-', 2);
        var core = versionAndPrerelease[0].Split('.');
        if (core.Length != 3 || core.Any(part => !IsNumericIdentifier(part, rejectLeadingZero: true))) return false;

        return versionAndPrerelease.Length == 1 ||
            AreValidIdentifiers(versionAndPrerelease[1], rejectNumericLeadingZero: true);
    }

    public static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool AreValidIdentifiers(string value, bool rejectNumericLeadingZero)
    {
        var identifiers = value.Split('.');
        return identifiers.All(identifier =>
            identifier.Length > 0 &&
            identifier.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') &&
            (!rejectNumericLeadingZero || !identifier.All(char.IsAsciiDigit) ||
             identifier.Length == 1 || identifier[0] != '0'));
    }

    private static bool IsNumericIdentifier(string value, bool rejectLeadingZero)
        => value.Length > 0 && value.All(char.IsAsciiDigit) &&
           (!rejectLeadingZero || value.Length == 1 || value[0] != '0');
}
