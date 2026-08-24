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

    /// <summary>Catalog 与 release metadata 共用的正式资产 URL 规则，只允许 ASCII DNS/严格 IPv4 和 HTTPS 443。</summary>
    public static bool IsSafeHttpsReleaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !HasOnlySafeAsciiUriCharacters(value)) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.IsDefaultPort ||
            uri.HostNameType == UriHostNameType.IPv6)
        {
            return false;
        }

        var schemeSeparator = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0) return false;
        var authorityStart = schemeSeparator + 3;
        var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
        var authority = authorityEnd < 0 ? value[authorityStart..] : value[authorityStart..authorityEnd];
        if (authority.EndsWith(":443", StringComparison.Ordinal))
            authority = authority[..^4];
        if (authority.Length is 0 or > 253 || authority.Contains(':') || authority.Contains('@')) return false;

        return IsStrictIpv4(authority) || IsAsciiDnsHost(authority);
    }

    private static bool HasOnlySafeAsciiUriCharacters(string value)
    {
        const string allowedPunctuation = "._~:/?@!$&'()*+,;=-";
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character > 0x7f) return false;
            if (char.IsAsciiLetterOrDigit(character) || allowedPunctuation.Contains(character)) continue;
            if (character != '%' || index + 2 >= value.Length ||
                !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static bool IsStrictIpv4(string host)
    {
        var octets = host.Split('.');
        return octets.Length == 4 && octets.All(octet =>
            octet.Length > 0 &&
            (octet.Length == 1 || octet[0] != '0') &&
            octet.All(char.IsAsciiDigit) &&
            int.TryParse(octet, out var value) && value <= 255);
    }

    private static bool IsAsciiDnsHost(string host)
    {
        if (!host.Any(char.IsAsciiLetter)) return false;
        return host.Split('.').All(label =>
            label.Length is >= 1 and <= 63 &&
            char.IsAsciiLetterOrDigit(label[0]) &&
            char.IsAsciiLetterOrDigit(label[^1]) &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

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
