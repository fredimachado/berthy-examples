using Microsoft.EntityFrameworkCore;
using Npgsql;

internal static class MigrateCommand
{
    internal const string ConnectionStringName = "ConnectionStrings:tododb";

    internal static async Task<int> RunAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        var connectionString = configuration["ConnectionStrings:tododb"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await Console.Error.WriteLineAsync($"Missing required configuration setting '{ConnectionStringName}'.");
            return 1;
        }

        try
        {
            var options = new DbContextOptionsBuilder<TodoDbContext>()
                .UseNpgsql(connectionString)
                .Options;

            await using var db = new TodoDbContext(options);
            await db.Database.MigrateAsync(cancellationToken);
            return 0;
        }
        catch (Exception ex)
        {
            // Never echo the connection string; report the failure class only.
            await Console.Error.WriteLineAsync($"Migration failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Plants a conflicting todos table so a later migrate fails. Used by AppHost gate tests.
    /// </summary>
    internal static async Task<int> PlantConflictAsync(
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var connectionString = configuration["ConnectionStrings:tododb"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await Console.Error.WriteLineAsync($"Missing required configuration setting '{ConnectionStringName}'.");
            return 1;
        }

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS todos (
                bogus integer PRIMARY KEY
            );
            """,
            conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return 0;
    }
}
