using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Todo.Tests;

[Collection(ServiceCollection.Name)]
public sealed class HealthTests
{
    private readonly ServiceFixture _services;

    public HealthTests(ServiceFixture services) => _services = services;

    [Fact]
    public async Task HealthReportsExactlyTheFailedDependencies()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        // Healthy stack.
        await using (var factory = CreateFactory(database.ConnectionString, _services.RedisConnectionString))
        {
            var client = factory.CreateClient();
            using var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("health-replica", response.Headers.GetValues("X-Served-By").Single());
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
            Assert.False(doc.RootElement.TryGetProperty("failed", out _));
        }

        // Redis only.
        await using (var factory = CreateFactory(database.ConnectionString, "localhost:1"))
        {
            var client = factory.CreateClient();
            using var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("health-replica", response.Headers.GetValues("X-Served-By").Single());
            AssertFailed(await response.Content.ReadAsStringAsync(), "redis");
        }

        // Postgres only.
        await using (var factory = CreateFactory(
            "Host=127.0.0.1;Port=1;Database=tododb;Username=todo;Password=todo-test-password",
            _services.RedisConnectionString))
        {
            var client = factory.CreateClient();
            using var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("health-replica", response.Headers.GetValues("X-Served-By").Single());
            AssertFailed(await response.Content.ReadAsStringAsync(), "postgres");
        }

        // Both, ordered postgres then redis.
        await using (var factory = CreateFactory(
            "Host=127.0.0.1;Port=1;Database=tododb;Username=todo;Password=todo-test-password",
            "localhost:1"))
        {
            var client = factory.CreateClient();
            using var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("health-replica", response.Headers.GetValues("X-Served-By").Single());
            AssertFailed(await response.Content.ReadAsStringAsync(), "postgres", "redis");
        }
    }

    [Fact]
    public async Task HealthUsesOneDeadlineForBothProbes()
    {
        var clock = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;

        async Task HangAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref startedCount) == 2)
            {
                started.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        var probes = new DependencyProbe[]
        {
            new("postgres", HangAsync),
            new("redis", HangAsync),
        };

        var healthTask = DependencyHealth.FailedDependenciesAsync(probes, clock, CancellationToken.None);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, startedCount);

        clock.Advance(TimeSpan.FromSeconds(2));

        var failed = await healthTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "postgres", "redis" }, failed);

        // Second case: one probe finishes before the shared deadline; only the hanging peer fails.
        clock = new FakeTimeProvider();
        started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        startedCount = 0;
        var redisHung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task OkThenSignalAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref startedCount) == 2)
            {
                started.TrySetResult();
            }

            await Task.CompletedTask;
        }

        async Task HangRedisAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref startedCount) == 2)
            {
                started.TrySetResult();
            }

            redisHung.TrySetResult();
            try
            {
                await release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        probes =
        [
            new("postgres", OkThenSignalAsync),
            new("redis", HangRedisAsync),
        ];

        healthTask = DependencyHealth.FailedDependenciesAsync(probes, clock, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await redisHung.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, startedCount);

        clock.Advance(TimeSpan.FromSeconds(2));
        failed = await healthTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "redis" }, failed);
    }

    [Fact]
    public async Task HealthLateSuccessAfterDeadlineStillReportsFailed()
    {
        var clock = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task SucceedAfterBudgetAsync(CancellationToken ct)
        {
            entered.TrySetResult();
            // Ignore cancellation so a late success can race the deadline classification.
            await finish.Task;
        }

        var probes = new DependencyProbe[]
        {
            new("postgres", SucceedAfterBudgetAsync),
            new("redis", async _ => await Task.CompletedTask),
        };

        var healthTask = DependencyHealth.FailedDependenciesAsync(probes, clock, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(2));
        // Complete successfully after the budget expired — must not clear the timeout failure.
        finish.TrySetResult();

        var failed = await healthTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "postgres" }, failed);
    }

    [Fact]
    public async Task HealthAndCacheShareOneRedisConnection()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        await using var factory = CreateFactory(database.ConnectionString, _services.RedisConnectionString);
        var redis = factory.Services.GetRequiredService<AppRedis>();

        var fromCachePath = await redis.GetMultiplexerAsync(CancellationToken.None);
        var client = factory.CreateClient();
        using (var health = await client.GetAsync("/health"))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        var afterHealth = await redis.GetMultiplexerAsync(CancellationToken.None);
        Assert.Same(fromCachePath, afterHealth);

        using var list = await client.GetAsync("/api/todos");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Same(fromCachePath, await redis.GetMultiplexerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HealthFirstRequestWithPendingRedisStillProbesPostgres()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        // Accept TCP but never complete the Redis handshake so ConnectAsync stays pending.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var heldClients = new List<TcpClient>();
        var acceptLoop = AcceptAndHoldAsync(listener, heldClients);

        try
        {
            await using var factory = CreateFactory(database.ConnectionString, $"localhost:{port}");
            var client = factory.CreateClient();

            var sw = Stopwatch.StartNew();
            using var response = await client.GetAsync("/health");
            sw.Stop();

            Assert.True(
                sw.Elapsed < TimeSpan.FromSeconds(5),
                $"cold Redis connect starved the health deadline: {sw.Elapsed}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            AssertFailed(await response.Content.ReadAsStringAsync(), "redis");
        }
        finally
        {
            TcpClient[] snapshot;
            lock (heldClients)
            {
                snapshot = heldClients.ToArray();
                heldClients.Clear();
            }

            foreach (var held in snapshot)
            {
                held.Dispose();
            }

            listener.Stop();
            try
            {
                await acceptLoop.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception)
            {
                // Listener stopped; accept loop may fault.
            }
        }
    }

    [Fact]
    public async Task HealthDeadlineDoesNotAwaitHeldCancellationCallbacks()
    {
        var clock = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task HangWithHeldCancelAsync(CancellationToken ct)
        {
            ct.Register(() =>
            {
                callbackEntered.TrySetResult();
                // Blocks Cancel/CancelAsync until released — must not stall the response path.
                releaseCallback.Task.GetAwaiter().GetResult();
            });
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        var probes = new DependencyProbe[]
        {
            new("postgres", HangWithHeldCancelAsync),
            new("redis", async _ => await Task.CompletedTask),
        };

        var healthTask = DependencyHealth.FailedDependenciesAsync(probes, clock, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(2));

        var failed = await healthTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "postgres" }, failed);

        // Callback may still be held; releasing cleans up the background cancel path.
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseCallback.TrySetResult();
    }

    [Fact]
    public async Task HealthWithoutTododbReportsPostgresFailed()
    {
        await using var factory = new ApiFactory(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:redis"] = _services.RedisConnectionString,
                ["BuildVersion"] = "v-health",
                ["BuildCommit"] = "health-test",
                ["HOSTNAME"] = "health-replica",
            });
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("health-replica", response.Headers.GetValues("X-Served-By").Single());
        AssertFailed(await response.Content.ReadAsStringAsync(), "postgres");
    }

    private static async Task AcceptAndHoldAsync(TcpListener listener, List<TcpClient> heldClients)
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                // Retain until the assertion finishes; dispose in the caller's teardown.
                lock (heldClients)
                {
                    heldClients.Add(client);
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private static ApiFactory CreateFactory(string postgres, string redis) =>
        new(new Dictionary<string, string?>
        {
            ["ConnectionStrings:tododb"] = postgres,
            ["ConnectionStrings:redis"] = redis,
            ["BuildVersion"] = "v-health",
            ["BuildCommit"] = "health-test",
            ["HOSTNAME"] = "health-replica",
        });

    private static void AssertFailed(string body, params string[] expected)
    {
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("unavailable", doc.RootElement.GetProperty("status").GetString());
        var failed = doc.RootElement.GetProperty("failed").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        Assert.Equal(expected, failed);
    }
}
