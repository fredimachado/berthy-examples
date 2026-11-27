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
        await using var host = await AspireHost.StartAsync(AppHostPath, cts.Token);

        // Capture first-seen resource states by polling describe — not when the test called wait.
        var transitions = await host.WatchTransitionsAsync(
            ["tododb", "migrate", "api"],
            untilResource: "api",
            untilStatePredicate: IsHealthyOrRunning,
            cts.Token);

        var dbReady = FirstTransition(transitions, "tododb", IsHealthyOrRunning);
        var migrateDone = FirstTransition(
            transitions,
            "migrate",
            state => IsFinished(state) || IsDown(state));
        var apiReady = FirstTransition(transitions, "api", IsHealthyOrRunning);

        // Sequence index is the ordering proof; timestamps alone can tie within one poll tick.
        Assert.True(
            dbReady.Index < migrateDone.Index,
            $"tododb ready (#{dbReady.Index} {dbReady.At:o} / {dbReady.State}) must precede migrate terminal (#{migrateDone.Index} {migrateDone.At:o} / {migrateDone.State})");
        Assert.True(
            migrateDone.Index < apiReady.Index,
            $"migrate terminal (#{migrateDone.Index} {migrateDone.At:o} / {migrateDone.State}) must precede api ready (#{apiReady.Index} {apiReady.At:o} / {apiReady.State})");

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

        // Real AppHost path: conflict resource WaitFor(database) then migrate WaitForCompletion(conflict).
        await using var host = await AspireHost.StartAsync(
            AppHostPath,
            cts.Token,
            new Dictionary<string, string?> { ["PlantMigrationConflict"] = "true" });

        await host.WaitUntilResourceAsync(
            "conflict",
            IsTerminalSuccess,
            cts.Token);

        await host.WaitUntilResourceAsync(
            "migrate",
            state => IsFinished(state) || IsDown(state) || state.Contains("Failed", StringComparison.OrdinalIgnoreCase),
            cts.Token);

        // Conflicting schema must prevent the CreateTodos baseline.
        var historyBefore = await host.ExecSqlAsync(
            "tododb",
            """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = '__EFMigrationsHistory';
            """,
            cts.Token);
        if (long.TryParse(historyBefore.Trim(), out var historyTables) && historyTables > 0)
        {
            var applied = await host.ExecSqlAsync(
                "tododb",
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

        // WaitForCompletion(migrate) must keep the API from accepting requests.
        await AssertApiDoesNotAcceptAsync(host, TimeSpan.FromSeconds(20), cts.Token);

        // Same database: remove only the fixture conflict, remigrate, then start the gated API.
        await host.ExecSqlAsync("tododb", "DROP TABLE IF EXISTS todos;", cts.Token);

        await host.ResourceCommandAsync("migrate", "restart", cts.Token);
        await host.WaitUntilResourceAsync(
            "migrate",
            state => IsTerminalSuccess(state) || IsDown(state) || IsFinished(state),
            cts.Token);

        // Confirm CreateTodos applied on the same DB after repair.
        var appliedAfter = await host.ExecSqlAsync(
            "tododb",
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
                IsHealthyOrRunning(api.State),
                $"api became {api.State} while migrate was expected to block it via WaitForCompletion");

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
                    // Timeout on connect counts as not accepting.
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

    private static ResourceTransition FirstTransition(
        IReadOnlyList<ResourceTransition> transitions,
        string resource,
        Func<string, bool> predicate)
    {
        for (var i = 0; i < transitions.Count; i++)
        {
            var t = transitions[i];
            if (string.Equals(t.Resource, resource, StringComparison.OrdinalIgnoreCase)
                && predicate(t.State))
            {
                return t with { Index = i };
            }
        }

        Assert.Fail(
            $"no transition for {resource} matching predicate; saw: {string.Join(", ", transitions.Select(t => $"{t.Resource}={t.State}@{t.At:HH:mm:ss.fff}"))}");
        return default;
    }

    private static bool IsHealthyOrRunning(string state) =>
        state.Contains("Healthy", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalSuccess(string state) =>
        string.Equals(state, "Finished", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "Exited", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinished(string state) =>
        state.Contains("Finished", StringComparison.OrdinalIgnoreCase)
        || state.Contains("Exited", StringComparison.OrdinalIgnoreCase);

    private static bool IsDown(string state) =>
        string.Equals(state, "Down", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "NotStarted", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "Finished", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "Exited", StringComparison.OrdinalIgnoreCase);
}

internal readonly record struct ResourceTransition(DateTimeOffset At, string Resource, string State, int Index = -1);

internal sealed class ResourceSnapshot
{
    public required string Name { get; init; }
    public required string State { get; init; }
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
}

internal sealed class AspireHost : IAsyncDisposable
{
    private readonly string _appHostPath;
    private readonly int _pid;
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

    public async Task WaitUntilResourceAsync(
        string resource,
        Func<string, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await GetResourceAsync(resource, cancellationToken);
            if (predicate(snapshot.State))
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    public async Task<List<ResourceTransition>> WatchTransitionsAsync(
        IReadOnlyList<string> resources,
        string untilResource,
        Func<string, bool> untilStatePredicate,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var transitions = new List<ResourceTransition>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;

            foreach (var resource in resources)
            {
                var snapshot = await GetResourceAsync(resource, cancellationToken);
                var key = $"{resource}|{snapshot.State}";
                if (seen.Add(key))
                {
                    transitions.Add(new ResourceTransition(now, resource, snapshot.State, transitions.Count));
                }

                if (string.Equals(resource, untilResource, StringComparison.OrdinalIgnoreCase)
                    && untilStatePredicate(snapshot.State))
                {
                    return transitions;
                }
            }

            await Task.Delay(200, cancellationToken);
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
                return string.Equals(name, resource, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(display, resource, StringComparison.OrdinalIgnoreCase)
                    || (name?.StartsWith(resource + "-", StringComparison.OrdinalIgnoreCase) ?? false);
            });
            if (element.ValueKind == JsonValueKind.Undefined)
            {
                element = resources[0];
            }
        }

        var state = ReadState(element);
        var name = element.TryGetProperty("name", out var nameEl)
            ? nameEl.GetString() ?? resource
            : resource;

        return new ResourceSnapshot
        {
            Name = name,
            State = state,
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

    public async Task<string> ExecSqlAsync(string databaseResource, string sql, CancellationToken cancellationToken)
    {
        // Connection strings are redacted from describe; use the postgres container Aspire started.
        var postgres = await GetResourceAsync("postgres", cancellationToken);
        var containerId = await ResolvePostgresContainerIdAsync(postgres, cancellationToken);
        var (user, password) = await ReadPostgresCredentialsAsync(containerId, cancellationToken);

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
        start.ArgumentList.Add(databaseResource);
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
                $"docker exec psql failed ({process.ExitCode}) on container {containerId}: {stderr}");
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

    private static string ReadState(JsonElement element)
    {
        if (element.TryGetProperty("healthStatus", out var health)
            && health.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(health.GetString())
            && !string.Equals(health.GetString(), "null", StringComparison.OrdinalIgnoreCase))
        {
            var healthText = health.GetString()!;
            if (element.TryGetProperty("state", out var stateWithHealth)
                && stateWithHealth.ValueKind == JsonValueKind.String)
            {
                return $"{stateWithHealth.GetString()}/{healthText}";
            }

            return healthText;
        }

        if (element.TryGetProperty("state", out var state))
        {
            if (state.ValueKind == JsonValueKind.String)
            {
                return state.GetString() ?? "";
            }

            if (state.ValueKind == JsonValueKind.Object
                && state.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? "";
            }
        }

        return "";
    }

    private static async Task<string> ResolvePostgresContainerIdAsync(
        ResourceSnapshot postgres,
        CancellationToken cancellationToken)
    {
        // Prefer an explicit container id from describe when Aspire exposes it.
        if (postgres.Root.TryGetProperty("properties", out var props))
        {
            foreach (var prop in props.EnumerateObject())
            {
                if (prop.Name.Contains("container", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                {
                    return prop.Value.GetString()!;
                }
            }
        }

        var name = postgres.Name;
        var (exit, stdout, stderr) = await RunProcessAsync(
            "docker",
            ["ps", "--format", "{{.ID}} {{.Names}}"],
            cancellationToken);
        if (exit != 0)
        {
            throw new InvalidOperationException($"docker ps failed ({exit}): {stderr}");
        }

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2
                && parts[1].Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }

        // Isolated AppHosts suffix resource names; match any running postgres:17 container started recently.
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Contains("postgres", StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }

        throw new InvalidOperationException(
            $"Could not resolve postgres container for resource '{name}'. docker ps:\n{stdout}");
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
            throw new InvalidOperationException($"POSTGRES_USER/PASSWORD missing in container {containerId}.");
        }

        return (user, password);
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
