using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;

namespace HephaestusWorkbench.Data;

public sealed class SqlitePluginInfoRepository : IPluginInfoRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SqlitePluginInfoRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(PluginInfo item, CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO plugin_info (id, name, version, type, path, entry, enabled)
            VALUES ($id, $name, $version, $type, $path, $entry, $enabled)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, version = excluded.version,
                type = excluded.type, path = excluded.path, entry = excluded.entry, enabled = excluded.enabled
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$name", item.Name);
        command.Parameters.AddWithValue("$version", item.Version);
        command.Parameters.AddWithValue("$type", item.Type);
        command.Parameters.AddWithValue("$path", item.Path);
        command.Parameters.AddWithValue("$entry", item.Entry);
        command.Parameters.AddWithValue("$enabled", item.Enabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PluginInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _factory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, version, type, path, entry, enabled FROM plugin_info ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PluginInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PluginInfo
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Version = reader.GetString(2),
                Type = reader.GetString(3),
                Path = reader.GetString(4),
                Entry = reader.GetString(5),
                Enabled = reader.GetInt32(6) != 0
            });
        }
        return result;
    }
}
