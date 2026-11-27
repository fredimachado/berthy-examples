using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Todo.Tests;

/// <summary>
/// Owns disposable Postgres 17 and Redis 7 containers with dynamic host ports.
/// </summary>
public sealed class ServiceFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private RedisContainer? _redis;

    public string PostgresConnectionString =>
        _postgres?.GetConnectionString()
        ?? throw new InvalidOperationException("Postgres container has not started.");

    public string RedisConnectionString =>
        _redis?.GetConnectionString()
        ?? throw new InvalidOperationException("Redis container has not started.");

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder("postgres:17")
            .WithDatabase("tododb")
            .WithUsername("todo")
            .WithPassword("todo-test-password")
            .Build();

        _redis = new RedisBuilder("redis:7").Build();

        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ServiceCollection : ICollectionFixture<ServiceFixture>
{
    public const string Name = "service-fixture";
}
