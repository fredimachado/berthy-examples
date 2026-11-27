using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Todo.Tests;

public sealed class AppHostGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(4);

    private static readonly string AppHostPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AppHost", "apphost.cs"));

    [Fact]
    public async Task ColdStartWaitsForDatabaseThenMigration()
    {
        using var cts = new CancellationTokenSource(Timeout);

        // Hold migrate at a test-controlled point: AppHost uses WithExplicitStart on migrate.
        await using var host = await AspireHost.StartAsync(
            AppHostPath,
            cts.Token,
            new Dictionary<string, string?> { ["HoldMigrateUntilStarted"] = "true" });

        await host.WaitAsync("tododb", "healthy", cts.Token);
        await host.WaitAsync("redis", "healthy", cts.Token);

        // While migrate is held, WaitForCompletion must keep the API from serving.
        await AssertApiDoesNotAcceptAsync(host, TimeSpan.FromSeconds(15), cts.Token);
        var heldMigrate = await host.GetResourceAsync("migrate", cts.Token);
        Assert.True(
            IsHeldBeforeStart(heldMigrate.State),
            $"migrate should still be held before explicit start, was '{heldMigrate.State}'");
        Assert.True(
            heldMigrate.ExitCode is null,
            $"migrate must not have exited before release; exitCode={heldMigrate.ExitCode}");

        // Release migration; require a successful exit before the API may serve.
        await host.ResourceCommandAsync("migrate", "start", cts.Token);
        var migrateExit = await host.WaitForExitCodeAsync("migrate", cts.Token);
        Assert.Equal(0, migrateExit);

        await host.WaitAsync("api", "healthy", cts.Token);

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

        // Real AppHost path: conflict → migrate WaitForCompletion(conflict) → api WaitForCompletion(migrate).
        await using var host = await AspireHost.StartAsync(
            AppHostPath,
            cts.Token,
            new Dictionary<string, string?> { ["PlantMigrationConflict"] = "true" });

        var conflictExit = await host.WaitForExitCodeAsync("conflict", cts.Token);
        Assert.Equal(0, conflictExit);

        // Initial migrate must reach a terminal nonzero exit — not NotStarted / never-launched.
        var failedExit = await host.WaitForExitCodeAsync("migrate", cts.Token);
        Assert.NotEqual(0, failedExit);

        var historyBefore = await host.ExecSqlOnOwnedPostgresAsync(
            """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory';
            """,
            cts.Token);
        if (long.TryParse(historyBefore.Trim(), out var historyTables) && historyTables > 0)
        {
            var applied = await host.ExecSqlOnOwnedPostgresAsync(
                """
                SELECT COUNT(*) FROM "__EFMigrationsHistory"
                WHERE "MigrationId" LIKE '%CreateTodos%';
                """,
                cts.Token);
            Assert.Equal("0", applied.Trim());
        }
        else
        {
            Assert.Equal("0", historyBefore.Trim());
        }

        await AssertApiDoesNotAcceptAsync(host, TimeSpan.FromSeconds(15), cts.Token);

        // Same owned database: remove only the fixture conflict, remigrate with exit 0, then API serves [].
        await host.ExecSqlOnOwnedPostgresAsync("DROP TABLE IF EXISTS todos;", cts.Token);

        await host.ResourceCommandAsync("migrate", "restart", cts.Token);
        var recoveredExit = await host.WaitForExitCodeAsync("migrate", cts.Token);
        Assert.Equal(0, recoveredExit);

        var appliedAfter = await host.ExecSqlOnOwnedPostgresAsync(
            """
            SELECT COUNT(*) FROM "__EFMigrationsHistory"
            WHERE "MigrationId" LIKE '%CreateTodos%';
            """,
            cts.Token);
        Assert.Equal("1", appliedAfter.Trim());

        await host.ResourceCommandAsync("api", "start", cts.Token);
        await host.WaitAsync("api", "healthy", cts.Token);

        var apiUrl = await host.GetHttpEndpointAsync("api", cts.Token);
        using var client = new HttpClient { BaseAddress = new Uri(apiUrl) };
        using var response = await client.GetAsync("/api/todos", cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    private static async Task AssertApiDoesNotAcceptAsync(
        AspireHost host,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        windowCts.CancelAfter(window);

        while (!windowCts.IsCancellationRequested)
        {
            var api = await host.GetResourceAsync("api", cancellationToken);
            Assert.False(
                IsServing(api.State),
                $"api became serving state '{api.State}' while migrate was expected to block it");

            if (api.TryGetHttpUrl(out var url))
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    using var response = await client.GetAsync($"{url}/api/todos", windowCts.Token);
                    Assert.Fail($"api accepted GET /api/todos with {(int)response.StatusCode} while blocked");
                }
                catch (HttpRequestException)
                {
                    // Expected: nothing accepting.
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Connect timeout counts as not accepting.
                }
            }

            try
            {
                await Task.Delay(500, windowCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>True only for healthy/running — never Unhealthy or NotStarted.</summary>
    private static bool IsServing(string state)
    {
        if (state.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase)
            || state.Contains("NotStarted", StringComparison.OrdinalIgnoreCase)
            || state.Contains("Waiting", StringComparison.OrdinalIgnoreCase)
            || state.Contains("Finished", StringComparison.OrdinalIgnoreCase)
            || state.Contains("Failed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = state.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Any(s =>
            string.Equals(s, "Healthy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "Running", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHeldBeforeStart(string state) =>
        state.Contains("NotStarted", StringComparison.OrdinalIgnoreCase)
        || state.Contains("Waiting", StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(state)
        || state.Contains("Hidden", StringComparison.OrdinalIgnoreCase);
}

internal sealed class ResourceSnapshot
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string State { get; init; }
    public int? ExitCode { get; init; }
    public required string Raw { get; init; }
    public JsonElement Root { get; init; }

    public bool TryGetHttpUrl(out string url)
    {
        url = "";
        if (!Root.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var entry in urls.EnumerateArray())
        {
            var name = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
            var value = entry.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.Equals(name, "http", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value))
            {
                url = value.TrimEnd('/');
                return true;
            }
        }

        foreach (var entry in urls.EnumerateArray())
        {
            var value = entry.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (value is not null && value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                url = value.TrimEnd('/');
                return true;
            }
        }

        return false;
    }

    public string? TryGetPropertyString(string propertyName)
    {
        if (!Root.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (props.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }
}

internal sealed class AspireHost : IAsyncDisposable
{
    private readonly string _appHostPath;
    private readonly int _pid;
    private string? _ownedPostgresContainerId;
    private string? _ownedPostgresResourceName;
    private bool _stopped;

    private AspireHost(string appHostPath, int pid)
    {
        _appHostPath = appHostPath;
        _pid = pid;
    }

    public static async Task<AspireHost> StartAsync(
        string appHostPath,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var (exit, stdout, stderr) = await RunAspireAsync(
            ["start", "--apphost", appHostPath, "--isolated", "--non-interactive", "--format", "Json", "--nologo"],
            cancellationToken,
            environment);
        if (exit != 0)
        {
            throw new InvalidOperationException($"aspire start failed ({exit}): {stdout}\n{stderr}");
        }

        using var doc = JsonDocument.Parse(ExtractJsonObject(stdout));
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

    public async Task<int> WaitForExitCodeAsync(string resource, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await GetResourceAsync(resource, cancellationToken);
            if (snapshot.ExitCode is int code)
            {
                // Exit codes are only meaningful after the process has actually launched and finished.
                Assert.False(
                    IsNeverLaunched(snapshot.State),
                    $"{resource} reported exitCode={code} while still never-launched ('{snapshot.State}')");
                return code;
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    public async Task<ResourceSnapshot> GetResourceAsync(string resource, CancellationToken cancellationToken)
    {
        var json = await DescribeAsync(resource, cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement element = root;
        if (root.TryGetProperty("resources", out var resources) && resources.GetArrayLength() > 0)
        {
            element = resources.EnumerateArray().FirstOrDefault(r =>
            {
                var name = r.TryGetProperty("name", out var n) ? n.GetString() : null;
                var display = r.TryGetProperty("displayName", out var d) ? d.GetString() : null;
                return string.Equals(display, resource, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, resource, StringComparison.OrdinalIgnoreCase)
                    || (name?.StartsWith(resource + "-", StringComparison.OrdinalIgnoreCase) ?? false);
            });
            if (element.ValueKind == JsonValueKind.Undefined)
            {
                element = resources[0];
            }
        }

        int? exitCode = null;
        if (element.TryGetProperty("exitCode", out var exitEl))
        {
            if (exitEl.ValueKind == JsonValueKind.Number && exitEl.TryGetInt32(out var n))
            {
                exitCode = n;
            }
            else if (exitEl.ValueKind == JsonValueKind.String
                && int.TryParse(exitEl.GetString(), out var parsed))
            {
                exitCode = parsed;
            }
        }

        return new ResourceSnapshot
        {
            Name = element.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? resource : resource,
            DisplayName = element.TryGetProperty("displayName", out var displayEl)
                ? displayEl.GetString() ?? resource
                : resource,
            State = ReadState(element),
            ExitCode = exitCode,
            Raw = json,
            Root = element.Clone(),
        };
    }

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

    public async Task ResourceCommandAsync(string resource, string command, CancellationToken cancellationToken)
    {
        await RunAspireCheckedAsync(
            ["resource", resource, command, "--apphost", _appHostPath, "--non-interactive", "--nologo"],
            cancellationToken);
    }

    public async Task<string> GetHttpEndpointAsync(string resource, CancellationToken cancellationToken)
    {
        var snapshot = await GetResourceAsync(resource, cancellationToken);
        if (!snapshot.TryGetHttpUrl(out var url))
        {
            throw new InvalidOperationException($"describe {resource} has no http url: {snapshot.Raw}");
        }

        return url;
    }

    public async Task<string> ExecSqlOnOwnedPostgresAsync(string sql, CancellationToken cancellationToken)
    {
        var (containerId, resourceName) = await ResolveOwnedPostgresContainerAsync(cancellationToken);
        var (user, password) = await ReadPostgresCredentialsAsync(containerId, cancellationToken);

        // Re-verify ownership immediately before destructive SQL.
        await AssertContainerStillOwnedAsync(containerId, resourceName, cancellationToken);

        var start = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add($"PGPASSWORD={password}");
        start.ArgumentList.Add(containerId);
        start.ArgumentList.Add("psql");
        start.ArgumentList.Add("-U");
        start.ArgumentList.Add(user);
        start.ArgumentList.Add("-d");
        start.ArgumentList.Add("tododb");
        start.ArgumentList.Add("-t");
        start.ArgumentList.Add("-A");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(sql);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start docker exec.");
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker exec psql failed ({process.ExitCode}) on owned container {containerId} ({resourceName}): {stderr}");
        }

        return stdout;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        var (exit, stdout, stderr) = await RunAspireAsync(
            ["stop", "--pid", _pid.ToString(), "--apphost", _appHostPath, "--non-interactive", "--nologo"],
            CancellationToken.None);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"aspire stop failed for owned AppHost PID {_pid} (exit {exit}). stderr: {stderr} stdout: {stdout}");
        }
    }

    private async Task<(string ContainerId, string ResourceName)> ResolveOwnedPostgresContainerAsync(
        CancellationToken cancellationToken)
    {
        if (_ownedPostgresContainerId is not null && _ownedPostgresResourceName is not null)
        {
            return (_ownedPostgresContainerId, _ownedPostgresResourceName);
        }

        var postgres = await GetResourceAsync("postgres", cancellationToken);
        var containerId = postgres.TryGetPropertyString("container.id");
        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw new InvalidOperationException(
                $"AppHost postgres resource '{postgres.Name}' has no properties.container.id; refusing SQL. describe={postgres.Raw}");
        }

        // Ownership: container name must equal the exact Aspire resource name for this AppHost.
        await AssertContainerStillOwnedAsync(containerId, postgres.Name, cancellationToken);
        _ownedPostgresContainerId = containerId;
        _ownedPostgresResourceName = postgres.Name;
        return (containerId, postgres.Name);
    }

    private static async Task AssertContainerStillOwnedAsync(
        string containerId,
        string expectedResourceName,
        CancellationToken cancellationToken)
    {
        var (exit, stdout, stderr) = await RunProcessAsync(
            "docker",
            ["inspect", "-f", "{{.Name}}", containerId],
            cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"Cannot verify ownership of container '{containerId}' for resource '{expectedResourceName}': {stderr}");
        }

        var containerName = stdout.Trim().TrimStart('/');
        if (!string.Equals(containerName, expectedResourceName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing SQL: container '{containerId}' is named '{containerName}', expected exact AppHost resource name '{expectedResourceName}'.");
        }
    }

    private static async Task<(string User, string Password)> ReadPostgresCredentialsAsync(
        string containerId,
        CancellationToken cancellationToken)
    {
        var (exit, stdout, stderr) = await RunProcessAsync(
            "docker",
            ["inspect", "-f", "{{range .Config.Env}}{{println .}}{{end}}", containerId],
            cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"docker inspect failed ({exit}): {stderr}");
        }

        string? user = null;
        string? password = null;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("POSTGRES_USER=", StringComparison.Ordinal))
            {
                user = line["POSTGRES_USER=".Length..];
            }
            else if (line.StartsWith("POSTGRES_PASSWORD=", StringComparison.Ordinal))
            {
                password = line["POSTGRES_PASSWORD=".Length..];
            }
        }

        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException($"POSTGRES_USER/PASSWORD missing in owned container {containerId}.");
        }

        return (user, password);
    }

    private static string ReadState(JsonElement element)
    {
        string? stateText = null;
        if (element.TryGetProperty("state", out var state))
        {
            if (state.ValueKind == JsonValueKind.String)
            {
                stateText = state.GetString();
            }
            else if (state.ValueKind == JsonValueKind.Object
                && state.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                stateText = text.GetString();
            }
        }

        string? healthText = null;
        if (element.TryGetProperty("healthStatus", out var health)
            && health.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(health.GetString())
            && !string.Equals(health.GetString(), "null", StringComparison.OrdinalIgnoreCase))
        {
            healthText = health.GetString();
        }

        if (stateText is not null && healthText is not null)
        {
            return $"{stateText}/{healthText}";
        }

        return stateText ?? healthText ?? "";
    }

    private static bool IsNeverLaunched(string state) =>
        state.Contains("NotStarted", StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(state);

    private static string ExtractJsonObject(string stdout)
    {
        var start = stdout.IndexOf('{');
        var end = stdout.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException($"No JSON object in aspire output: {stdout}");
        }

        return stdout[start..(end + 1)];
    }

    private async Task RunAspireCheckedAsync(string[] args, CancellationToken cancellationToken)
    {
        var (exit, stdout, stderr) = await RunAspireAsync(args, cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"aspire {args[0]} failed ({exit}): {stdout}\n{stderr}");
        }
    }

    private static Task<(int Exit, string StdOut, string StdErr)> RunAspireAsync(
        string[] args,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null) =>
        RunProcessAsync("aspire", args, cancellationToken, environment);

    private static async Task<(int Exit, string StdOut, string StdErr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                start.Environment[key] = value;
            }
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
            throw new InvalidOperationException($"Failed to start {fileName}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
