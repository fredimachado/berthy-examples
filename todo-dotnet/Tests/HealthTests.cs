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
        var postgresRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task OkThenSignalAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref startedCount) == 2)
            {
                started.TrySetResult();
            }

            return Task.CompletedTask;
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

        try
        {
            healthTask = DependencyHealth.FailedDependenciesAsync(
                probes,
                clock,
                CancellationToken.None,
                name =>
                {
                    if (name == "postgres")
                    {
                        postgresRecorded.TrySetResult();
                    }
                });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await redisHung.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, startedCount);

            // onSucceededInBudget fires after the in-budget CAS — only then advance.
            await postgresRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromSeconds(2));
            failed = await healthTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "redis" }, failed);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task HealthSchedulesProbesIndependentlyOfSyncWorkBeforeFirstAwait()
    {
        var clock = new FakeTimeProvider();
        var postgresHolding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var redisStartedWhileHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var holdPostgres = new ManualResetEventSlim(false);

        Task PostgresBlocksSynchronouslyBeforeAwait(CancellationToken ct)
        {
            postgresHolding.TrySetResult();
            // Unbounded hold (ct only): a sequential scheduler must fail the Redis wait below,
            // not sneak through after a timed-out Postgres probe.
            holdPostgres.Wait(ct);
            return Task.CompletedTask;
        }

        async Task RedisSignalsThenHangsAsync(CancellationToken ct)
        {
            // Fail fast if postgres was already released — that implies sequential scheduling.
            Assert.False(
                holdPostgres.IsSet,
                "Redis started after Postgres was released; probes were not scheduled concurrently.");
            redisStartedWhileHeld.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        var probes = new DependencyProbe[]
        {
            new("postgres", PostgresBlocksSynchronouslyBeforeAwait),
            new("redis", RedisSignalsThenHangsAsync),
        };

        // Run on a worker so an inline/sequential probe loop cannot block this test thread.
        var healthTask = Task.Run(
            () => DependencyHealth.FailedDependenciesAsync(probes, clock, CancellationToken.None),
            CancellationToken.None);

        try
        {
            await postgresHolding.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Redis must signal while Postgres is still held (before release / hold timeout).
            await redisStartedWhileHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(holdPostgres.IsSet);

            clock.Advance(DependencyHealth.Deadline);
            var failed = await healthTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "postgres", "redis" }, failed);
        }
        finally
        {
            holdPostgres.Set();
        }
    }

    [Fact]
    public async Task HealthLateSuccessAfterDeadlineStillReportsFailed()
    {
        var clock = new FakeTimeProvider();
        var redisRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task RedisOkFirstAsync(CancellationToken ct) => Task.CompletedTask;

        async Task AdvancePastDeadlineThenSucceedAsync(CancellationToken ct)
        {
            // Wait until redis's in-budget success was recorded, then advance and succeed late.
            await redisRecorded.Task.WaitAsync(ct);
            clock.Advance(TimeSpan.FromSeconds(2));
            await Task.CompletedTask;
        }

        var probes = new DependencyProbe[]
        {
            new("postgres", AdvancePastDeadlineThenSucceedAsync),
            new("redis", RedisOkFirstAsync),
        };

        var failed = await DependencyHealth.FailedDependenciesAsync(
            probes,
            clock,
            CancellationToken.None,
            name =>
            {
                if (name == "redis")
                {
                    redisRecorded.TrySetResult();
                }
            });
        Assert.Equal(new[] { "postgres" }, failed);
    }

    [Fact]
    public async Task HealthLateSuccessRejectedByElapsedCheckWhileDeadlineTimerHeld()
    {
        // Clock advances past the deadline while the deadline timer callback is held, so the
        // deadline loop cannot mark Pending→Failed yet. Postgres's late success must fail the
        // elapsed-time check in TryRecordSuccess specifically — WhenAll can finish while the
        // timer is still held; without the elapsed check the failure list would be empty.
        using var holdTimerCallbacks = new ManualResetEventSlim(false);
        var timerCallbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new HoldableFakeTimeProvider(holdTimerCallbacks, timerCallbackEntered);
        var redisRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPostgresFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? advanceTask = null;

        Task RedisOkFirstAsync(CancellationToken ct) => Task.CompletedTask;

        async Task LateSuccessAfterClockPastDeadlineAsync(CancellationToken ct)
        {
            await redisRecorded.Task.WaitAsync(ct);
            await allowPostgresFinish.Task.WaitAsync(ct);
            await Task.CompletedTask;
        }

        var probes = new DependencyProbe[]
        {
            new("postgres", LateSuccessAfterClockPastDeadlineAsync),
            new("redis", RedisOkFirstAsync),
        };

        var healthTask = DependencyHealth.FailedDependenciesAsync(
            probes,
            clock,
            CancellationToken.None,
            name =>
            {
                if (name == "redis")
                {
                    redisRecorded.TrySetResult();
                }
            });

        try
        {
            await redisRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var startedAt = clock.GetTimestamp();
            // Advance on a worker: time moves forward, then the deadline timer callback blocks.
            advanceTask = Task.Run(() => clock.Advance(DependencyHealth.Deadline), CancellationToken.None);
            await timerCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(clock.GetElapsedTime(startedAt) >= DependencyHealth.Deadline);

            // Late success while deadline path is still blocked; await health before releasing.
            allowPostgresFinish.TrySetResult();
            var failed = await healthTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "postgres" }, failed);
        }
        finally
        {
            allowPostgresFinish.TrySetResult();
            holdTimerCallbacks.Set();
            // No timeout: disposal must wait until Advance leaves Wait() on the gate.
            if (advanceTask is not null)
            {
                await advanceTask;
            }
        }
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
            // Stop listener and await accept loop before disposing retained clients.
            listener.Stop();
            try
            {
                await acceptLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Listener stopped; accept loop may fault.
            }

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
        }
    }

    [Fact]
    public async Task HealthDeadlineDoesNotAwaitHeldCancellationCallbacks()
    {
        var clock = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var redisRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? advanceTask = null;

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
            new("redis", static _ => Task.CompletedTask),
        };

        try
        {
            var healthTask = DependencyHealth.FailedDependenciesAsync(
                probes,
                clock,
                CancellationToken.None,
                name =>
                {
                    if (name == "redis")
                    {
                        redisRecorded.TrySetResult();
                    }
                });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await redisRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Advance on a worker so a sync cancel callback cannot block this thread before finally.
            advanceTask = Task.Run(() => clock.Advance(TimeSpan.FromSeconds(2)), CancellationToken.None);

            var failed = await healthTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "postgres" }, failed);

            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseCallback.TrySetResult();
            if (advanceTask is not null)
            {
                await advanceTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
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

/// <summary>
/// FakeTimeProvider that advances wall time first, then holds each due timer callback until released.
/// </summary>
file sealed class HoldableFakeTimeProvider : TimeProvider
{
    private readonly FakeTimeProvider _inner = new();
    private readonly ManualResetEventSlim _releaseCallbacks;
    private readonly TaskCompletionSource _callbackEntered;

    public HoldableFakeTimeProvider(
        ManualResetEventSlim releaseCallbacks,
        TaskCompletionSource callbackEntered)
    {
        _releaseCallbacks = releaseCallbacks;
        _callbackEntered = callbackEntered;
    }

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public override long GetTimestamp() => _inner.GetTimestamp();

    public void Advance(TimeSpan delta) => _inner.Advance(delta);

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        return _inner.CreateTimer(
            s =>
            {
                _callbackEntered.TrySetResult();
                _releaseCallbacks.Wait();
                callback(s);
            },
            state,
            dueTime,
            period);
    }
}
