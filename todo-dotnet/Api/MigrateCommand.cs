using Microsoft.EntityFrameworkCore;

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
}
