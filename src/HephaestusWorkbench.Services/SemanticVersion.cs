namespace HephaestusWorkbench.Services;

/// <summary>
/// 实现宿主扩展契约需要的 SemVer 2.0.0 解析和顺序比较。构建元数据不参与排序，
/// 正式版高于预发布版，数字标识使用字符串长度比较，避免合法超大数字溢出整数范围。
/// </summary>
internal sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private readonly string[] _core;
    private readonly string[] _prerelease;

    private SemanticVersion(string[] core, string[] prerelease)
    {
        _core = core;
        _prerelease = prerelease;
    }

    public bool IsPrerelease => _prerelease.Length > 0;

    public static SemanticVersion Parse(string value)
        => TryParse(value, out var version)
            ? version
            : throw new InvalidDataException($"扩展版本不是有效的语义化版本：{value}");

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var buildParts = value.Split('+');
        if (buildParts.Length > 2 || buildParts.Any(string.IsNullOrEmpty)) return false;
        var versionParts = buildParts[0].Split('-', 2);
        var core = versionParts[0].Split('.');
        if (core.Length != 3 || core.Any(part => !IsNumeric(part, rejectLeadingZero: true))) return false;

        var prerelease = versionParts.Length == 1 ? [] : versionParts[1].Split('.');
        if (prerelease.Any(identifier => !IsIdentifier(identifier))) return false;
        if (buildParts.Length == 2 && buildParts[1].Split('.').Any(identifier => !IsIdentifier(identifier, false)))
            return false;

        version = new SemanticVersion(core, prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        for (var index = 0; index < _core.Length; index++)
        {
            var comparison = CompareNumeric(_core[index], other._core[index]);
            if (comparison != 0) return comparison;
        }

        if (_prerelease.Length == 0 || other._prerelease.Length == 0)
            return _prerelease.Length == other._prerelease.Length ? 0 : _prerelease.Length == 0 ? 1 : -1;

        for (var index = 0; index < Math.Min(_prerelease.Length, other._prerelease.Length); index++)
        {
            var left = _prerelease[index];
            var right = other._prerelease[index];
            var leftNumeric = left.All(char.IsAsciiDigit);
            var rightNumeric = right.All(char.IsAsciiDigit);
            int comparison;
            if (leftNumeric && rightNumeric)
                comparison = CompareNumeric(left, right);
            else if (leftNumeric != rightNumeric)
                comparison = leftNumeric ? -1 : 1;
            else
                comparison = string.CompareOrdinal(left, right);
            if (comparison != 0) return comparison;
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static int CompareNumeric(string left, string right)
    {
        var lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
    }

    private static bool IsIdentifier(string value, bool rejectNumericLeadingZero = true)
        => value.Length > 0 &&
           value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') &&
           (!rejectNumericLeadingZero || !value.All(char.IsAsciiDigit) || value.Length == 1 || value[0] != '0');

    private static bool IsNumeric(string value, bool rejectLeadingZero)
        => value.Length > 0 && value.All(char.IsAsciiDigit) &&
           (!rejectLeadingZero || value.Length == 1 || value[0] != '0');
}
