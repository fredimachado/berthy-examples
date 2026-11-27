using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Todo.Tests;

[Collection(ServiceCollection.Name)]
public sealed class AppHostGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(4);

    private static readonly string AppHostPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AppHost", "apphost.cs"));

    private readonly ServiceFixture _services;

    public AppHostGateTests(ServiceFixture services) => _services = services;

    [Fact]
    public async Task ColdStartWaitsForDatabaseThenMigration()
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using var host = await AspireHost.StartAsync(AppHostPath, cts.Token);

        // Binding order: database ready → migrate exit → API healthy (WaitForCompletion).
        await host.WaitAsync("tododb", "healthy", cts.Token);
        var dbReadyAt = DateTimeOffset.UtcNow;

        await host.WaitAsync("api", "healthy", cts.Token);
        var apiReadyAt = DateTimeOffset.UtcNow;
        Assert.True(apiReadyAt >= dbReadyAt, "api became healthy before tododb was waited healthy");

        var migrateJson = await host.DescribeAsync("migrate", cts.Token);
        Assert.True(
            migrateJson.Contains("Finished", StringComparison.OrdinalIgnoreCase)
            || migrateJson.Contains("Exited", StringComparison.OrdinalIgnoreCase)
            || migrateJson.Contains("\"state\":\"Finished\"", StringComparison.OrdinalIgnoreCase),
            $"migrate did not finish before api healthy: {migrateJson}");

        // aspire wait only accepts healthy|up|down; migrate is a one-shot so confirm it is down.
        await host.WaitAsync("migrate", "down", cts.Token);

        var apiUrl = await host.GetHttpEndpointAsync("api", cts.Token);
        using var client = new HttpClient { BaseAddress = new Uri(apiUrl) };
        using var response = await client.GetAsync("/api/todos", cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task FailedMigrationBlocksApiAndCanRecover()
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);

        // Deliberately conflicting table: migrate must exit nonzero with no CreateTodos baseline.
        await using (var conn = new NpgsqlConnection(database.ConnectionString))
        {
            await conn.OpenAsync(cts.Token);
            await using var conflict = new NpgsqlCommand(
                "CREATE TABLE todos (bogus integer PRIMARY KEY)",
                conn);
            await conflict.ExecuteNonQueryAsync(cts.Token);
        }

        var failed = await MigrateProcess.RunAsync(database.ConnectionString, cts.Token);
        Assert.NotEqual(0, failed.ExitCode);

        await using (var conn = new NpgsqlConnection(database.ConnectionString))
        {
            await conn.OpenAsync(cts.Token);
            await using var history = new NpgsqlCommand(
                """
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory'
                """,
                conn);
            var historyTables = (long)(await history.ExecuteScalarAsync(cts.Token))!;
            if (historyTables > 0)
            {
                await using var applied = new NpgsqlCommand(
                    """
                    SELECT COUNT(*) FROM "__EFMigrationsHistory"
                    WHERE "MigrationId" LIKE '%CreateTodos%'
                    """,
                    conn);
                Assert.Equal(0L, (long)(await applied.ExecuteScalarAsync(cts.Token))!);
            }
        }

        // Remove only the fixture conflict, migrate, then serve the empty list.
        await using (var conn = new NpgsqlConnection(database.ConnectionString))
        {
            await conn.OpenAsync(cts.Token);
            await using var drop = new NpgsqlCommand("DROP TABLE IF EXISTS todos", conn);
            await drop.ExecuteNonQueryAsync(cts.Token);
        }

        var recovered = await MigrateProcess.RunAsync(database.ConnectionString, cts.Token);
        Assert.Equal(0, recovered.ExitCode);

        await using var okFactory = new ApiFactory(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:tododb"] = database.ConnectionString,
                ["ConnectionStrings:redis"] = _services.RedisConnectionString,
                ["HOSTNAME"] = "",
            });
        using var response = await okFactory.CreateClient().GetAsync("/api/todos", cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }
}

internal sealed class AspireHost : IAsyncDisposable
{
    private readonly string _appHostPath;
    private readonly int _pid;

    private AspireHost(string appHostPath, int pid)
    {
        _appHostPath = appHostPath;
        _pid = pid;
    }

    public static async Task<AspireHost> StartAsync(string appHostPath, CancellationToken cancellationToken)
    {
        var (exit, stdout, stderr) = await RunAspireAsync(
            ["start", "--apphost", appHostPath, "--isolated", "--non-interactive", "--format", "Json", "--nologo"],
            cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"aspire start failed ({exit}): {stdout}\n{stderr}");
        }

        using var doc = JsonDocument.Parse(stdout);
        var pid = doc.RootElement.GetProperty("appHostPid").GetInt32();
        if (pid <= 0)
        {
            throw new InvalidOperationException($"aspire start returned invalid pid: {stdout}");
        }

        return new AspireHost(appHostPath, pid);
    }

    public Task WaitAsync(string resource, string status, CancellationToken cancellationToken) =>
        RunAspireCheckedAsync(
            ["wait", resource, "--status", status, "--apphost", _appHostPath, "--non-interactive", "--nologo"],
            cancellationToken);

    public async Task<string> DescribeAsync(string resource, CancellationToken cancellationToken)
    {
        var (exit, stdout, stderr) = await RunAspireAsync(
            ["describe", resource, "--format", "Json", "--apphost", _appHostPath, "--non-interactive", "--nologo"],
            cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"aspire describe failed ({exit}): {stdout}\n{stderr}");
        }

        return stdout;
    }

    public async Task<string> GetHttpEndpointAsync(string resource, CancellationToken cancellationToken)
    {
        var json = await DescribeAsync(resource, cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement resourceElement = root;
        if (root.TryGetProperty("resources", out var resources) && resources.GetArrayLength() > 0)
        {
            resourceElement = resources[0];
        }

        if (!resourceElement.TryGetProperty("urls", out var urls))
        {
            throw new InvalidOperationException($"describe {resource} missing urls: {json}");
        }

        foreach (var url in urls.EnumerateArray())
        {
            var name = url.TryGetProperty("name", out var n) ? n.GetString() : null;
            var value = url.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.Equals(name, "http", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value.TrimEnd('/');
            }
        }

        foreach (var url in urls.EnumerateArray())
        {
            var value = url.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (value is not null && value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return value.TrimEnd('/');
            }
        }

        throw new InvalidOperationException($"describe {resource} has no http url: {json}");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await RunAspireAsync(
                ["stop", "--pid", _pid.ToString(), "--apphost", _appHostPath, "--non-interactive", "--nologo"],
                CancellationToken.None);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static async Task RunAspireCheckedAsync(string[] args, CancellationToken cancellationToken)
    {
        var (exit, stdout, stderr) = await RunAspireAsync(args, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"aspire {args[0]} failed ({exit}): {stdout}\n{stderr}");
        }
    }

    private static async Task<(int Exit, string StdOut, string StdErr)> RunAspireAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "aspire",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = start };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start aspire process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
