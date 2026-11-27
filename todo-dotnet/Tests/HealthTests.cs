using System.Net;
using System.Text.Json;
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
