using Npgsql;

namespace Todo.Tests;

[Collection(ServiceCollection.Name)]
public sealed class SchemaTests
{
    private readonly ServiceFixture _services;

    public SchemaTests(ServiceFixture services) => _services = services;

    [Fact]
    public async Task MigrationCanRunTwiceWithoutChangingRows()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);

        var first = await MigrateProcess.RunAsync(database.ConnectionString);
        Assert.Equal(0, first.ExitCode);

        await using (var conn = new NpgsqlConnection(database.ConnectionString))
        {
            await conn.OpenAsync();
            await using var insert = new NpgsqlCommand(
                "INSERT INTO todos (title) VALUES ('keep-me') RETURNING id, title, done",
                conn);
            await using var reader = await insert.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var id = reader.GetInt64(0);
            var title = reader.GetString(1);
            var done = reader.GetBoolean(2);
            await reader.CloseAsync();

            var second = await MigrateProcess.RunAsync(database.ConnectionString);
            Assert.Equal(0, second.ExitCode);

            await using var select = new NpgsqlCommand(
                "SELECT id, title, done FROM todos WHERE id = @id",
                conn);
            select.Parameters.AddWithValue("id", id);
            await using var after = await select.ExecuteReaderAsync();
            Assert.True(await after.ReadAsync());
            Assert.Equal(id, after.GetInt64(0));
            Assert.Equal(title, after.GetString(1));
            Assert.Equal(done, after.GetBoolean(2));
            Assert.False(await after.ReadAsync());
        }
    }

    [Fact]
    public async Task ConcurrentMigratorsProduceOneSchema()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);

        var first = MigrateProcess.RunAsync(database.ConnectionString);
        var second = MigrateProcess.RunAsync(database.ConnectionString);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.Equal(0, r.ExitCode));

        await using var conn = new NpgsqlConnection(database.ConnectionString);
        await conn.OpenAsync();

        await using (var history = new NpgsqlCommand(
            """
            SELECT COUNT(*) FROM "__EFMigrationsHistory"
            """,
            conn))
        {
            var applied = (long)(await history.ExecuteScalarAsync())!;
            Assert.Equal(1, applied);
        }

        await using (var table = new NpgsqlCommand(
            """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = 'todos'
            """,
            conn))
        {
            var tables = (long)(await table.ExecuteScalarAsync())!;
            Assert.Equal(1, tables);
        }

        await using var probe = new NpgsqlCommand(
            "INSERT INTO todos (title) VALUES ('concurrent-ok') RETURNING id",
            conn);
        var id = await probe.ExecuteScalarAsync();
        Assert.NotNull(id);
    }

    [Fact]
    public async Task DatabaseEnforcesTitleLength()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        var migrate = await MigrateProcess.RunAsync(database.ConnectionString);
        Assert.Equal(0, migrate.ExitCode);

        await using var conn = new NpgsqlConnection(database.ConnectionString);
        await conn.OpenAsync();

        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var empty = new NpgsqlCommand(
                "INSERT INTO todos (title) VALUES ('')",
                conn);
            await empty.ExecuteNonQueryAsync();
        });

        var tooLong = new string('x', 201);
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO todos (title) VALUES (@title)",
                conn);
            cmd.Parameters.AddWithValue("title", tooLong);
            await cmd.ExecuteNonQueryAsync();
        });

        var ok = new string('y', 200);
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO todos (title) VALUES (@title) RETURNING id",
            conn))
        {
            cmd.Parameters.AddWithValue("title", ok);
            var id = await cmd.ExecuteScalarAsync();
            Assert.NotNull(id);
        }
    }
}

internal sealed class IsolatedDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;

    private IsolatedDatabase(string adminConnectionString, string databaseName, string connectionString)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static async Task<IsolatedDatabase> CreateAsync(string adminConnectionString)
    {
        var name = "todo_" + Guid.NewGuid().ToString("N");
        await using (var conn = new NpgsqlConnection(adminConnectionString))
        {
            await conn.OpenAsync();
            await using var create = new NpgsqlCommand(
                $"CREATE DATABASE \"{name}\"",
                conn);
            await create.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = name,
        };
        return new IsolatedDatabase(adminConnectionString, name, builder.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync();
        await using (var terminate = new NpgsqlCommand(
            """
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = @name AND pid <> pg_backend_pid()
            """,
            conn))
        {
            terminate.Parameters.AddWithValue("name", _databaseName);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{_databaseName}\"",
            conn);
        await drop.ExecuteNonQueryAsync();
    }
}
