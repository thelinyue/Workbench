namespace HephaestusWorkbench.Data;

internal static class SqliteValue
{
    public static string Date(DateTime value) => value.ToUniversalTime().ToString("O");
    public static string? Date(DateTime? value) => value?.ToUniversalTime().ToString("O");
    public static DateTime ParseDate(object value) => DateTime.Parse((string)value, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime();
    public static DateTime? ParseNullableDate(object? value) => value is null or DBNull ? null : ParseDate(value);
}
