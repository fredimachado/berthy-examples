using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StackExchange.Redis;

namespace Todo.Tests;

[Collection(ServiceCollection.Name)]
public sealed class CacheTests
{
    private readonly ServiceFixture _services;

    public CacheTests(ServiceFixture services) => _services = services;

    [Fact]
    public async Task EmptyAndPopulatedListsMissThenHit()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);
        await RedisAdmin.FlushListKeyAsync(_services.RedisConnectionString);

        await using var factory = CreateFactory(database.ConnectionString);
        var client = factory.CreateClient();

        using (var firstEmpty = await client.GetAsync("/api/todos"))
        {
            Assert.Equal(HttpStatusCode.OK, firstEmpty.StatusCode);
            Assert.Equal("miss", GetCacheHeader(firstEmpty));
            Assert.Equal("[]", await firstEmpty.Content.ReadAsStringAsync());
        }

        using (var secondEmpty = await client.GetAsync("/api/todos"))
        {
            Assert.Equal(HttpStatusCode.OK, secondEmpty.StatusCode);
            Assert.Equal("hit", GetCacheHeader(secondEmpty));
            Assert.Equal("[]", await secondEmpty.Content.ReadAsStringAsync());
        }

        var ttlEmpty = await RedisAdmin.ListTtlAsync(_services.RedisConnectionString);
        Assert.True(ttlEmpty > TimeSpan.Zero && ttlEmpty <= TimeSpan.FromSeconds(60), $"empty TTL={ttlEmpty}");

        await using (var conn = new NpgsqlConnection(database.ConnectionString))
        {
            await conn.OpenAsync();
            await using var insert = new NpgsqlCommand(
                "INSERT INTO todos (title) VALUES ('cached') RETURNING id",
                conn);
            await insert.ExecuteScalarAsync();
        }

        // Direct DB insert leaves the empty list cached; invalidate via a successful mutation path
        // is covered elsewhere. Clear so the populated miss fills from Postgres.
        await RedisAdmin.FlushListKeyAsync(_services.RedisConnectionString);

        string? populatedBody;
        using (var firstPopulated = await client.GetAsync("/api/todos"))
        {
            Assert.Equal(HttpStatusCode.OK, firstPopulated.StatusCode);
            Assert.Equal("miss", GetCacheHeader(firstPopulated));
            populatedBody = await firstPopulated.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(populatedBody);
            Assert.Equal(1, doc.RootElement.GetArrayLength());
            Assert.Equal("cached", doc.RootElement[0].GetProperty("title").GetString());
        }

        using (var secondPopulated = await client.GetAsync("/api/todos"))
        {
            Assert.Equal(HttpStatusCode.OK, secondPopulated.StatusCode);
            Assert.Equal("hit", GetCacheHeader(secondPopulated));
            Assert.Equal(populatedBody, await secondPopulated.Content.ReadAsStringAsync());
        }

        var ttlPopulated = await RedisAdmin.ListTtlAsync(_services.RedisConnectionString);
        Assert.True(
            ttlPopulated > TimeSpan.Zero && ttlPopulated <= TimeSpan.FromSeconds(60),
            $"populated TTL={ttlPopulated}");
    }

    [Fact]
    public async Task CommittedMutationsWithSuccessfulInvalidationMissNext()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);
        await RedisAdmin.FlushListKeyAsync(_services.RedisConnectionString);

        await using var factory = CreateFactory(database.ConnectionString);
        var client = factory.CreateClient();

        await PrimeHitAsync(client);

        using (var created = await PostJsonAsync(client, """{"title":"from-post"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using (var afterPost = await client.GetAsync("/api/todos"))
        {
            Assert.Equal("miss", GetCacheHeader(afterPost));
            using var doc = JsonDocument.Parse(await afterPost.Content.ReadAsStringAsync());
            Assert.Contains(
                doc.RootElement.EnumerateArray(),
                e => e.GetProperty("title").GetString() == "from-post");
        }

        using (var hitAfterPost = await client.GetAsync("/api/todos"))
        {
            Assert.Equal("hit", GetCacheHeader(hitAfterPost));
        }

        long id;
        using (var list = await client.GetAsync("/api/todos"))
        {
            using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            id = doc.RootElement.EnumerateArray()
                .Single(e => e.GetProperty("title").GetString() == "from-post")
                .GetProperty("id")
                .GetInt64();
        }

        await PrimeHitAsync(client);

        using (var patched = await PatchJsonAsync(client, id, """{"done":true}"""))
        {
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        }

        using (var afterPatch = await client.GetAsync("/api/todos"))
        {
            Assert.Equal("miss", GetCacheHeader(afterPatch));
            using var doc = JsonDocument.Parse(await afterPatch.Content.ReadAsStringAsync());
            var row = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetInt64() == id);
            Assert.True(row.GetProperty("done").GetBoolean());
        }

        using (var hitAfterPatch = await client.GetAsync("/api/todos"))
        {
            Assert.Equal("hit", GetCacheHeader(hitAfterPatch));
        }

        await PrimeHitAsync(client);

        using (var rejected = await PatchJsonAsync(client, id, "{}"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        }

        using (var stillHit = await client.GetAsync("/api/todos"))
        {
            Assert.Equal("hit", GetCacheHeader(stillHit));
        }

        await PrimeHitAsync(client);

        using (var deleted = await client.DeleteAsync($"/api/todos/{id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var afterDelete = await client.GetAsync("/api/todos"))
        {
            Assert.Equal("miss", GetCacheHeader(afterDelete));
            using var doc = JsonDocument.Parse(await afterDelete.Content.ReadAsStringAsync());
            Assert.DoesNotContain(
                doc.RootElement.EnumerateArray(),
                e => e.GetProperty("id").GetInt64() == id);
        }

        using (var hitAfterDelete = await client.GetAsync("/api/todos"))
        {
            Assert.Equal("hit", GetCacheHeader(hitAfterDelete));
        }
    }

    [Fact]
    public async Task FailedDeleteKeepsCommittedWriteAndCanServeStaleHit()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        var apiRedis = await RedisAcl.ProvisionApiUserAsync(_services.RedisConnectionString);
        try
        {
            await RedisAdmin.FlushListKeyAsync(apiRedis.AdminConnectionString);

            var seedId = await SeedTodoAsync(database.ConnectionString, "before-patch");

            await using var factory = CreateFactory(database.ConnectionString, apiRedis.ApiConnectionString);
            var client = factory.CreateClient();

            string primedBody;
            using (var miss = await client.GetAsync("/api/todos"))
            {
                Assert.Equal("miss", GetCacheHeader(miss));
                primedBody = await miss.Content.ReadAsStringAsync();
            }

            using (var hit = await client.GetAsync("/api/todos"))
            {
                Assert.Equal("hit", GetCacheHeader(hit));
                Assert.Equal(primedBody, await hit.Content.ReadAsStringAsync());
            }

            var before = await RedisAdmin.ReadListSnapshotAsync(apiRedis.AdminConnectionString);
            Assert.NotNull(before.Bytes);
            Assert.True(before.ExpireAtUnixMs > 0, $"PEXPIRETIME before={before.ExpireAtUnixMs}");

            await RedisAcl.DenyDeleteAsync(apiRedis.AdminConnectionString, apiRedis.Username);

            using (var patched = await PatchJsonAsync(client, seedId, """{"title":"after-patch"}"""))
            {
                Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
                using var doc = JsonDocument.Parse(await patched.Content.ReadAsStringAsync());
                Assert.Equal("after-patch", doc.RootElement.GetProperty("title").GetString());
            }

            await using (var conn = new NpgsqlConnection(database.ConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "SELECT title FROM todos WHERE id = @id",
                    conn);
                cmd.Parameters.AddWithValue("id", seedId);
                Assert.Equal("after-patch", (string)(await cmd.ExecuteScalarAsync())!);
            }

            var afterFailedDel = await RedisAdmin.ReadListSnapshotAsync(apiRedis.AdminConnectionString);
            Assert.Equal(before.Bytes, afterFailedDel.Bytes);
            Assert.Equal(before.ExpireAtUnixMs, afterFailedDel.ExpireAtUnixMs);

            using (var stale = await client.GetAsync("/api/todos"))
            {
                Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
                Assert.Equal("hit", GetCacheHeader(stale));
                Assert.Equal(primedBody, await stale.Content.ReadAsStringAsync());
                using var doc = JsonDocument.Parse(primedBody);
                Assert.Equal("before-patch", doc.RootElement[0].GetProperty("title").GetString());
            }

            await RedisAcl.AllowDeleteAsync(
                apiRedis.AdminConnectionString,
                apiRedis.Username,
                apiRedis.Password);
            await RedisAdmin.ExpireListKeyAsync(apiRedis.AdminConnectionString);

            using (var miss = await client.GetAsync("/api/todos"))
            {
                Assert.Equal("miss", GetCacheHeader(miss));
                using var doc = JsonDocument.Parse(await miss.Content.ReadAsStringAsync());
                Assert.Equal("after-patch", doc.RootElement[0].GetProperty("title").GetString());
            }
        }
        finally
        {
            await RedisAcl.DeleteUserAsync(_services.RedisConnectionString, apiRedis.Username);
        }
    }

    [Fact]
    public async Task InvalidateIgnoresRequestCancellationDuringAcquisition()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);
        await RedisAdmin.FlushListKeyAsync(_services.RedisConnectionString);

        await using var factory = CreateFactory(database.ConnectionString);
        var cache = factory.Services.GetRequiredService<TodoListCache>();

        await RedisAdmin.SetListBytesAsync(_services.RedisConnectionString, "[]"u8.ToArray());
        Assert.True((await RedisAdmin.ReadRawAsync(_services.RedisConnectionString)).HasValue);

        using var requestCt = new CancellationTokenSource();
        cache.BeforeInvalidateRedisAsync = () =>
        {
            // Simulate client disconnect while invalidation is acquiring Redis.
            requestCt.Cancel();
            return Task.CompletedTask;
        };

        await cache.InvalidateAsync(requestCt.Token);
        cache.BeforeInvalidateRedisAsync = null;

        var remaining = await RedisAdmin.ReadRawAsync(_services.RedisConnectionString);
        Assert.False(remaining.HasValue);
    }

    [Fact]
    public async Task AnOlderFillCannotRestoreADeletedValue()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);
        await RedisAdmin.FlushListKeyAsync(_services.RedisConnectionString);

        await using var factory = CreateFactory(database.ConnectionString);
        var client = factory.CreateClient();

        var cache = factory.Services.GetRequiredService<TodoListCache>();
        var readReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.AfterDatabaseReadAsync = async ct =>
        {
            readReached.TrySetResult();
            await releaseFill.Task.WaitAsync(ct);
        };

        var slowList = client.GetAsync("/api/todos");
        await readReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using (var created = await PostJsonAsync(client, """{"title":"written-during-fill"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        releaseFill.TrySetResult();
        using (var slowResponse = await slowList)
        {
            Assert.Equal(HttpStatusCode.OK, slowResponse.StatusCode);
            Assert.Equal("miss", GetCacheHeader(slowResponse));
        }

        cache.AfterDatabaseReadAsync = null;

        using (var next = await client.GetAsync("/api/todos"))
        {
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            using var doc = JsonDocument.Parse(await next.Content.ReadAsStringAsync());
            Assert.Contains(
                doc.RootElement.EnumerateArray(),
                e => e.GetProperty("title").GetString() == "written-during-fill");
        }
    }

    [Fact]
    public async Task LiveFillTokenIsNotReplacedByConcurrentReader()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);
        await RedisAdmin.FlushListKeyAsync(_services.RedisConnectionString);

        await using var factory = CreateFactory(database.ConnectionString);
        var client = factory.CreateClient();

        var cache = factory.Services.GetRequiredService<TodoListCache>();
        var readReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseOnce = 0;
        cache.AfterDatabaseReadAsync = async ct =>
        {
            // Only the first miss pauses; a concurrent reader must not deadlock on the same barrier.
            if (Interlocked.Exchange(ref pauseOnce, 1) != 0)
            {
                return;
            }

            readReached.TrySetResult();
            await releaseFill.Task.WaitAsync(ct);
        };

        var first = client.GetAsync("/api/todos");
        await readReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var token = await RedisAdmin.ReadRawAsync(_services.RedisConnectionString);
        Assert.True(TodoListCache.IsFillToken(token), $"expected fill token, got {token}");

        // Second GET must leave the first reader's token alone and still return a miss.
        using (var second = await client.GetAsync("/api/todos"))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal("miss", GetCacheHeader(second));
            Assert.Equal("[]", await second.Content.ReadAsStringAsync());
        }

        var tokenAfterSecond = await RedisAdmin.ReadRawAsync(_services.RedisConnectionString);
        Assert.Equal(token, tokenAfterSecond);

        releaseFill.TrySetResult();
        using (var firstResponse = await first)
        {
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            Assert.Equal("miss", GetCacheHeader(firstResponse));
        }

        cache.AfterDatabaseReadAsync = null;

        using (var hit = await client.GetAsync("/api/todos"))
        {
            Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
            Assert.Equal("hit", GetCacheHeader(hit));
            Assert.Equal("[]", await hit.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData(
        """[{"id":1,"title":null,"done":false,"created_at":"2020-01-01T00:00:00Z","updated_at":"2020-01-01T00:00:00Z"}]""")]
    public async Task MalformedCachedListIsRejectedAndRefilled(string malformedJson)
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        await using var factory = CreateFactory(database.ConnectionString);
        var client = factory.CreateClient();

        // Seed after ApiFactory construction — the factory flushes todo:list on create.
        await RedisAdmin.SetListBytesAsync(
            _services.RedisConnectionString,
            Encoding.UTF8.GetBytes(malformedJson));

        using (var miss = await client.GetAsync("/api/todos"))
        {
            Assert.Equal(HttpStatusCode.OK, miss.StatusCode);
            Assert.Equal("miss", GetCacheHeader(miss));
            Assert.Equal("[]", await miss.Content.ReadAsStringAsync());
        }

        using (var hit = await client.GetAsync("/api/todos"))
        {
            Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
            Assert.Equal("hit", GetCacheHeader(hit));
            Assert.Equal("[]", await hit.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task BrokenFillTokenIsRepairedOnRead()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        await using var factory = CreateFactory(database.ConnectionString);
        var client = factory.CreateClient();

        Assert.False(TodoListCache.IsFillToken("tok:broken"));
        await RedisAdmin.SetListBytesAsync(
            _services.RedisConnectionString,
            "tok:broken"u8.ToArray());

        using (var miss = await client.GetAsync("/api/todos"))
        {
            Assert.Equal(HttpStatusCode.OK, miss.StatusCode);
            Assert.Equal("miss", GetCacheHeader(miss));
            Assert.Equal("[]", await miss.Content.ReadAsStringAsync());
        }

        using (var hit = await client.GetAsync("/api/todos"))
        {
            Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
            Assert.Equal("hit", GetCacheHeader(hit));
            Assert.Equal("[]", await hit.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task RedisFailureFallsBackToPostgres()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        var deadRedis = "localhost:1";
        await using (var factory = CreateFactory(database.ConnectionString, deadRedis))
        {
            var client = factory.CreateClient();

            using (var created = await PostJsonAsync(client, """{"title":"no-redis"}"""))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }

            long id;
            using (var list = await client.GetAsync("/api/todos"))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                Assert.Equal("miss", GetCacheHeader(list));
                using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
                id = doc.RootElement.EnumerateArray().Single().GetProperty("id").GetInt64();
                Assert.Equal("no-redis", doc.RootElement[0].GetProperty("title").GetString());
            }

            using (var patched = await PatchJsonAsync(client, id, """{"done":true}"""))
            {
                Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
            }

            using (var deleted = await client.DeleteAsync($"/api/todos/{id}"))
            {
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            }

            using (var empty = await client.GetAsync("/api/todos"))
            {
                Assert.Equal("miss", GetCacheHeader(empty));
                Assert.Equal("[]", await empty.Content.ReadAsStringAsync());
            }
        }

        // Seed corrupt bytes after ApiFactory so the constructor flush cannot wipe them.
        await using (var factory = CreateFactory(database.ConnectionString))
        {
            var client = factory.CreateClient();
            await RedisAdmin.SetListBytesAsync(_services.RedisConnectionString, "{not-json"u8.ToArray());

            using (var miss = await client.GetAsync("/api/todos"))
            {
                Assert.Equal("miss", GetCacheHeader(miss));
                Assert.Equal("[]", await miss.Content.ReadAsStringAsync());
            }

            using (var hit = await client.GetAsync("/api/todos"))
            {
                Assert.Equal("hit", GetCacheHeader(hit));
                Assert.Equal("[]", await hit.Content.ReadAsStringAsync());
            }
        }
    }

    private ApiFactory CreateFactory(string postgresConnectionString, string? redisConnectionString = null) =>
        new(new Dictionary<string, string?>
        {
            ["ConnectionStrings:tododb"] = postgresConnectionString,
            ["ConnectionStrings:redis"] = redisConnectionString ?? _services.RedisConnectionString,
            ["BuildVersion"] = "v-test",
            ["BuildCommit"] = "cache-test",
            ["HOSTNAME"] = "",
        });

    private static async Task PrimeHitAsync(HttpClient client)
    {
        using var first = await client.GetAsync("/api/todos");
        var header = GetCacheHeader(first);
        if (header == "hit")
        {
            return;
        }

        Assert.Equal("miss", header);
        using var hit = await client.GetAsync("/api/todos");
        Assert.Equal("hit", GetCacheHeader(hit));
    }

    private static string GetCacheHeader(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("X-Cache", out var values));
        return values.Single();
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string body)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await client.PostAsync("/api/todos", content);
    }

    private static async Task<HttpResponseMessage> PatchJsonAsync(HttpClient client, long id, string body)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await client.PatchAsync($"/api/todos/{id}", content);
    }

    private static async Task<long> SeedTodoAsync(string connectionString, string title)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO todos (title) VALUES (@title) RETURNING id",
            conn);
        cmd.Parameters.AddWithValue("title", title);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}

internal static class RedisAdmin
{
    internal const string ListKey = "todo:list";

    internal static async Task FlushListKeyAsync(string connectionString)
    {
        using var mux = await ConnectAsync(connectionString);
        await mux.GetDatabase().KeyDeleteAsync(ListKey);
    }

    internal static async Task<TimeSpan> ListTtlAsync(string connectionString)
    {
        using var mux = await ConnectAsync(connectionString);
        var ttl = await mux.GetDatabase().KeyTimeToLiveAsync(ListKey);
        Assert.True(ttl.HasValue, "todo:list missing TTL");
        return ttl.Value;
    }

    internal static async Task<(byte[]? Bytes, long ExpireAtUnixMs)> ReadListSnapshotAsync(
        string connectionString)
    {
        using var mux = await ConnectAsync(connectionString);
        var db = mux.GetDatabase();
        var value = await db.StringGetAsync(ListKey);
        Assert.True(value.HasValue);
        var expireAt = (long)await db.ExecuteAsync("PEXPIRETIME", ListKey);
        Assert.True(expireAt > 0, $"PEXPIRETIME={expireAt}");
        return ((byte[]?)value, expireAt);
    }

    internal static async Task<RedisValue> ReadRawAsync(string connectionString)
    {
        using var mux = await ConnectAsync(connectionString);
        return await mux.GetDatabase().StringGetAsync(ListKey);
    }

    internal static async Task SetListBytesAsync(string connectionString, byte[] bytes)
    {
        using var mux = await ConnectAsync(connectionString);
        await mux.GetDatabase().StringSetAsync(ListKey, bytes, TimeSpan.FromSeconds(60));
    }

    internal static async Task ExpireListKeyAsync(string connectionString)
    {
        using var mux = await ConnectAsync(connectionString);
        // EXPIRE 0 deletes immediately; avoids a 60s sleep.
        Assert.True(await mux.GetDatabase().KeyExpireAsync(ListKey, TimeSpan.Zero));
    }

    private static async Task<ConnectionMultiplexer> ConnectAsync(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }
}

internal sealed class RedisAclUser(
    string username,
    string password,
    string adminConnectionString,
    string apiConnectionString)
{
    public string Username { get; } = username;
    public string Password { get; } = password;
    public string AdminConnectionString { get; } = adminConnectionString;
    public string ApiConnectionString { get; } = apiConnectionString;
}

internal static class RedisAcl
{
    internal static async Task<RedisAclUser> ProvisionApiUserAsync(string adminConnectionString)
    {
        var username = "todoapi_" + Guid.NewGuid().ToString("N")[..12];
        const string password = "todo-cache-test-secret";
        using var mux = await ConnectionMultiplexer.ConnectAsync(adminConnectionString);
        var db = mux.GetDatabase();
        // Full key access except we toggle delete commands in the failing-delete scenario.
        await db.ExecuteAsync(
            "ACL",
            "SETUSER",
            username,
            "on",
            $">{password}",
            "~*",
            "&*",
            "+@all");

        var parsed = ConfigurationOptions.Parse(adminConnectionString);
        var endpoint = parsed.EndPoints[0].ToString();
        var apiConnectionString =
            $"{endpoint},user={username},password={password},abortConnect=false";
        return new RedisAclUser(username, password, adminConnectionString, apiConnectionString);
    }

    internal static async Task DenyDeleteAsync(string adminConnectionString, string username)
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(adminConnectionString);
        // StackExchange.Redis KeyDelete prefers UNLINK when the server supports it.
        await mux.GetDatabase().ExecuteAsync("ACL", "SETUSER", username, "-del", "-unlink");
    }

    internal static async Task AllowDeleteAsync(
        string adminConnectionString,
        string username,
        string password)
    {
        using var mux = await ConnectionMultiplexer.ConnectAsync(adminConnectionString);
        // Re-state the user so +del/+unlink are not shadowed by an earlier -rule.
        await mux.GetDatabase().ExecuteAsync(
            "ACL",
            "SETUSER",
            username,
            "reset",
            "on",
            $">{password}",
            "~*",
            "&*",
            "+@all");
    }

    internal static async Task DeleteUserAsync(string adminConnectionString, string username)
    {
        try
        {
            using var mux = await ConnectionMultiplexer.ConnectAsync(adminConnectionString);
            await mux.GetDatabase().ExecuteAsync("ACL", "DELUSER", username);
        }
        catch (Exception)
        {
            // Best-effort cleanup for a disposable test user.
        }
    }
}
