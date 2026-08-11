using HephaestusWorkbench.Core.Repositories;

namespace HephaestusWorkbench.Data;

public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteSettingsStore(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : (string)value;
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO app_settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
