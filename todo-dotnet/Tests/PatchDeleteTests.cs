using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Todo.Tests;

[Collection(ServiceCollection.Name)]
public sealed class PatchDeleteTests
{
    private readonly ServiceFixture _services;

    public PatchDeleteTests(ServiceFixture services) => _services = services;

    [Fact]
    public async Task PatchChangesOnlySuppliedFields()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        var seedId = await SeedTodoAsync(
            database.ConnectionString,
            title: "original",
            done: false,
            createdAt: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            updatedAt: new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero));

        await using var factory = CreateFactory(database.ConnectionString, replica: "");
        var client = factory.CreateClient();

        using (var titleOnly = await PatchJsonAsync(client, seedId, """{"title":"  renamed  "}"""))
        {
            Assert.Equal(HttpStatusCode.OK, titleOnly.StatusCode);
            using var doc = await JsonDocument.ParseAsync(await titleOnly.Content.ReadAsStreamAsync());
            Assert.Equal(seedId, doc.RootElement.GetProperty("id").GetInt64());
            Assert.Equal("renamed", doc.RootElement.GetProperty("title").GetString());
            Assert.False(doc.RootElement.GetProperty("done").GetBoolean());
            Assert.Equal(
                "2020-01-01T00:00:00Z",
                NormalizeInstant(doc.RootElement.GetProperty("created_at").GetString()));
            var updatedAt = ParseInstant(doc.RootElement.GetProperty("updated_at").GetString());
            Assert.True(updatedAt > new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero));
        }

        var afterTitle = await ReadTodoAsync(database.ConnectionString, seedId);
        Assert.Equal("renamed", afterTitle.Title);
        Assert.False(afterTitle.Done);

        using (var doneOnly = await PatchJsonAsync(client, seedId, """{"done":true}"""))
        {
            Assert.Equal(HttpStatusCode.OK, doneOnly.StatusCode);
            using var doc = await JsonDocument.ParseAsync(await doneOnly.Content.ReadAsStreamAsync());
            Assert.Equal("renamed", doc.RootElement.GetProperty("title").GetString());
            Assert.True(doc.RootElement.GetProperty("done").GetBoolean());
        }

        using (var both = await PatchJsonAsync(client, seedId, """{"title":"both","done":false}"""))
        {
            Assert.Equal(HttpStatusCode.OK, both.StatusCode);
            using var doc = await JsonDocument.ParseAsync(await both.Content.ReadAsStreamAsync());
            Assert.Equal("both", doc.RootElement.GetProperty("title").GetString());
            Assert.False(doc.RootElement.GetProperty("done").GetBoolean());
        }

        using (var explicitFalse = await PatchJsonAsync(client, seedId, """{"done":false}"""))
        {
            Assert.Equal(HttpStatusCode.OK, explicitFalse.StatusCode);
            using var doc = await JsonDocument.ParseAsync(await explicitFalse.Content.ReadAsStreamAsync());
            Assert.False(doc.RootElement.GetProperty("done").GetBoolean());
        }

        // Null title leaves title alone; done is applied.
        using (var nullPlusValue = await PatchJsonAsync(client, seedId, """{"title":null,"done":true}"""))
        {
            Assert.Equal(HttpStatusCode.OK, nullPlusValue.StatusCode);
            using var doc = await JsonDocument.ParseAsync(await nullPlusValue.Content.ReadAsStreamAsync());
            Assert.Equal("both", doc.RootElement.GetProperty("title").GetString());
            Assert.True(doc.RootElement.GetProperty("done").GetBoolean());
        }

        var final = await ReadTodoAsync(database.ConnectionString, seedId);
        Assert.Equal(seedId, final.Id);
        Assert.Equal("both", final.Title);
        Assert.True(final.Done);
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), final.CreatedAt);
        Assert.True(final.UpdatedAt > new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task ConcurrentPartialPatchesPreserveBothEdits()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        var seedId = await SeedTodoAsync(
            database.ConnectionString,
            title: "seed",
            done: false,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow);

        await using var factory = CreateFactory(database.ConnectionString, replica: "");
        // Distinct clients → distinct handlers; scoped DbContexts use separate pool connections.
        var clientTitle = factory.CreateClient();
        var clientDone = factory.CreateClient();

        await using var holder = new NpgsqlConnection(database.ConnectionString);
        await holder.OpenAsync();
        await using var holderTx = await holder.BeginTransactionAsync();
        int holderPid;
        await using (var pidCmd = new NpgsqlCommand("SELECT pg_backend_pid()", holder, (NpgsqlTransaction)holderTx))
        {
            holderPid = (int)(await pidCmd.ExecuteScalarAsync())!;
        }

        await using (var lockCmd = new NpgsqlCommand(
            "SELECT id FROM todos WHERE id = $1 FOR UPDATE",
            holder,
            (NpgsqlTransaction)holderTx))
        {
            lockCmd.Parameters.AddWithValue(seedId);
            Assert.Equal(seedId, (long)(await lockCmd.ExecuteScalarAsync())!);
        }

        var titleTask = PatchJsonAsync(clientTitle, seedId, """{"title":"from-title"}""");
        var doneTask = PatchJsonAsync(clientDone, seedId, """{"done":true}""");

        try
        {
            await WaitUntilBothApiSessionsLockWaitAsync(
                database.ConnectionString,
                holderPid,
                TimeSpan.FromSeconds(15));
        }
        catch
        {
            await holderTx.RollbackAsync();
            // Drain in-flight requests so dispose does not hang on held locks.
            try { await Task.WhenAll(titleTask, doneTask); } catch { /* observed below */ }
            throw;
        }

        await holderTx.CommitAsync();

        using var titleResponse = await titleTask;
        using var doneResponse = await doneTask;
        Assert.Equal(HttpStatusCode.OK, titleResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, doneResponse.StatusCode);

        using var titleDoc = await JsonDocument.ParseAsync(await titleResponse.Content.ReadAsStreamAsync());
        using var doneDoc = await JsonDocument.ParseAsync(await doneResponse.Content.ReadAsStreamAsync());

        Assert.Equal("from-title", titleDoc.RootElement.GetProperty("title").GetString());
        Assert.True(doneDoc.RootElement.GetProperty("done").GetBoolean());

        var stored = await ReadTodoAsync(database.ConnectionString, seedId);
        Assert.Equal("from-title", stored.Title);
        Assert.True(stored.Done);

        var titleMatchesStored =
            titleDoc.RootElement.GetProperty("id").GetInt64() == stored.Id
            && titleDoc.RootElement.GetProperty("title").GetString() == stored.Title
            && titleDoc.RootElement.GetProperty("done").GetBoolean() == stored.Done
            && ParseInstant(titleDoc.RootElement.GetProperty("updated_at").GetString()) == stored.UpdatedAt;

        var doneMatchesStored =
            doneDoc.RootElement.GetProperty("id").GetInt64() == stored.Id
            && doneDoc.RootElement.GetProperty("title").GetString() == stored.Title
            && doneDoc.RootElement.GetProperty("done").GetBoolean() == stored.Done
            && ParseInstant(doneDoc.RootElement.GetProperty("updated_at").GetString()) == stored.UpdatedAt;

        Assert.True(
            titleMatchesStored || doneMatchesStored,
            "at least one whole response must equal the stored row");
    }

    [Fact]
    public async Task InvalidPatchLeavesRowIntact()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        var seedId = await SeedTodoAsync(
            database.ConnectionString,
            title: "keep-me",
            done: false,
            createdAt: new DateTimeOffset(2021, 5, 5, 12, 0, 0, TimeSpan.Zero),
            updatedAt: new DateTimeOffset(2021, 5, 5, 12, 0, 0, TimeSpan.Zero));

        await using var factory = CreateFactory(database.ConnectionString, replica: "replica-patch");
        var client = factory.CreateClient();

        var before = await ReadTodoAsync(database.ConnectionString, seedId);

        var cases = new (string Name, string Path, string Body, HttpStatusCode Status, string? ExactError)[]
        {
            ("empty object", $"/api/todos/{seedId}", "{}", HttpStatusCode.UnprocessableEntity,
                "give a title, done or both"),
            ("literal null", $"/api/todos/{seedId}", "null", HttpStatusCode.UnprocessableEntity,
                "give a title, done or both"),
            ("only null members", $"/api/todos/{seedId}", """{"title":null,"done":null}""",
                HttpStatusCode.UnprocessableEntity, "give a title, done or both"),
            ("invalid title", $"/api/todos/{seedId}", """{"title":""}""",
                HttpStatusCode.UnprocessableEntity, "title must be 1 to 200 characters"),
            ("done wrong type", $"/api/todos/{seedId}", """{"done":"yes"}""",
                HttpStatusCode.BadRequest, null),
            ("unknown field", $"/api/todos/{seedId}", """{"done":true,"extra":1}""",
                HttpStatusCode.BadRequest, "invalid JSON: json: unknown field \"extra\""),
            ("id not a number", "/api/todos/abc", """{"done":true}""",
                HttpStatusCode.BadRequest, "id must be a positive number"),
            ("id zero", "/api/todos/0", """{"done":true}""",
                HttpStatusCode.BadRequest, "id must be a positive number"),
            ("id negative", "/api/todos/-3", """{"done":true}""",
                HttpStatusCode.BadRequest, "id must be a positive number"),
            ("id overflow", "/api/todos/9223372036854775808", """{"done":true}""",
                HttpStatusCode.BadRequest, "id must be a positive number"),
        };

        foreach (var tc in cases)
        {
            using var response = await PatchJsonAsync(client, tc.Path, tc.Body);
            Assert.True(
                response.StatusCode == tc.Status,
                $"{tc.Name}: status={(int)response.StatusCode}, want={(int)tc.Status}");
            Assert.Equal(
                new[] { "application/json" },
                response.Content.Headers.GetValues("Content-Type").ToArray());
            Assert.Equal("replica-patch", response.Headers.GetValues("X-Served-By").Single());

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var error = doc.RootElement.GetProperty("error").GetString();
            Assert.False(string.IsNullOrEmpty(error));
            if (tc.ExactError is not null)
            {
                Assert.Equal(tc.ExactError, error);
            }
        }

        // Body over 8192 bytes → 413; row untouched.
        var over = Encoding.UTF8.GetBytes("{\"done\":true,\"pad\":\"" + new string('x', 8200) + "\"}");
        using (var tooBig = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/todos/{seedId}")
        {
            Content = new ByteArrayContent(over)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            },
        }))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
            await AssertExactJsonErrorAsync(tooBig, "request body too large");
        }

        var after = await ReadTodoAsync(database.ConnectionString, seedId);
        Assert.Equal(before.Title, after.Title);
        Assert.Equal(before.Done, after.Done);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task PaddedIdRejectedOnPatchAndDelete()
    {
        // Go strconv.ParseInt rejects whitespace; "%2042" must not become id 42.
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        var seedId = await SeedTodoAsync(
            database.ConnectionString,
            title: "pad-target",
            done: false,
            createdAt: new DateTimeOffset(2022, 3, 3, 0, 0, 0, TimeSpan.Zero),
            updatedAt: new DateTimeOffset(2022, 3, 3, 0, 0, 0, TimeSpan.Zero));

        await using var factory = CreateFactory(database.ConnectionString, replica: "");
        var client = factory.CreateClient();
        var before = await ReadTodoAsync(database.ConnectionString, seedId);

        var padded = $"%20{seedId}";
        using (var patch = await PatchJsonAsync(client, $"/api/todos/{padded}", """{"done":true}"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
            await AssertExactJsonErrorAsync(patch, "id must be a positive number");
        }

        using (var delete = await client.DeleteAsync($"/api/todos/{padded}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
            await AssertExactJsonErrorAsync(delete, "id must be a positive number");
        }

        var after = await ReadTodoAsync(database.ConnectionString, seedId);
        Assert.Equal(before.Title, after.Title);
        Assert.Equal(before.Done, after.Done);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(1, await CountTodosAsync(database.ConnectionString));
    }

    [Fact]
    public async Task PatchArrayBodyHasEmptyFieldWrongType()
    {
        // Go Decode into struct: top-level array → UnmarshalTypeError with Field "".
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        var seedId = await SeedTodoAsync(
            database.ConnectionString,
            title: "array-body",
            done: false,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow);

        await using var factory = CreateFactory(database.ConnectionString, replica: "");
        var client = factory.CreateClient();
        var before = await ReadTodoAsync(database.ConnectionString, seedId);

        using var response = await PatchJsonAsync(client, seedId, "[]");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertExactJsonErrorAsync(response, """invalid JSON: field "" has the wrong type""");

        var after = await ReadTodoAsync(database.ConnectionString, seedId);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.False(after.Done);
    }

    [Fact]
    public async Task PatchTrailingCommaIsSyntaxErrorNotWrongType()
    {
        // Syntax is checked before field types so {"done":"yes",} is not a done-type error.
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        var seedId = await SeedTodoAsync(
            database.ConnectionString,
            title: "comma-body",
            done: false,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow);

        await using var factory = CreateFactory(database.ConnectionString, replica: "");
        var client = factory.CreateClient();
        var before = await ReadTodoAsync(database.ConnectionString, seedId);

        using var response = await PatchJsonAsync(client, seedId, """{"done":"yes",}""");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertExactJsonErrorAsync(response, "invalid JSON: syntax error");

        var after = await ReadTodoAsync(database.ConnectionString, seedId);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.False(after.Done);
    }

    [Fact]
    public async Task DeleteRemovesExactlyOneRow()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        var keepId = await SeedTodoAsync(
            database.ConnectionString,
            title: "keep",
            done: false,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow);
        var dropId = await SeedTodoAsync(
            database.ConnectionString,
            title: "drop",
            done: true,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow);

        await using var factory = CreateFactory(database.ConnectionString, replica: "");
        var client = factory.CreateClient();

        using (var deleted = await client.DeleteAsync($"/api/todos/{dropId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Empty(await deleted.Content.ReadAsByteArrayAsync());
        }

        using (var again = await client.DeleteAsync($"/api/todos/{dropId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
            await AssertExactJsonErrorAsync(again, "todo not found");
        }

        using (var patchMissing = await PatchJsonAsync(client, dropId, """{"done":true}"""))
        {
            Assert.Equal(HttpStatusCode.NotFound, patchMissing.StatusCode);
            await AssertExactJsonErrorAsync(patchMissing, "todo not found");
        }

        Assert.Equal(1, await CountTodosAsync(database.ConnectionString));
        var kept = await ReadTodoAsync(database.ConnectionString, keepId);
        Assert.Equal("keep", kept.Title);
    }

    private ApiFactory CreateFactory(string connectionString, string replica) =>
        new(new Dictionary<string, string?>
        {
            ["ConnectionStrings:tododb"] = connectionString,
            ["ConnectionStrings:redis"] = _services.RedisConnectionString,
            ["BuildVersion"] = "v-test",
            ["BuildCommit"] = "patch-test",
            ["HOSTNAME"] = replica,
        });

    private static async Task<HttpResponseMessage> PatchJsonAsync(HttpClient client, long id, string body) =>
        await PatchJsonAsync(client, $"/api/todos/{id}", body);

    private static async Task<HttpResponseMessage> PatchJsonAsync(HttpClient client, string path, string body)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, path) { Content = content });
    }

    private static async Task AssertExactJsonErrorAsync(HttpResponseMessage response, string message)
    {
        Assert.Equal(
            new[] { "application/json" },
            response.Content.Headers.GetValues("Content-Type").ToArray());
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(message, doc.RootElement.GetProperty("error").GetString());
    }

    private static async Task<long> SeedTodoAsync(
        string connectionString,
        string title,
        bool done,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO todos (title, done, created_at, updated_at)
            VALUES ($1, $2, $3, $4)
            RETURNING id
            """,
            conn);
        cmd.Parameters.AddWithValue(title);
        cmd.Parameters.AddWithValue(done);
        cmd.Parameters.AddWithValue(createdAt);
        cmd.Parameters.AddWithValue(updatedAt);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<(long Id, string Title, bool Done, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)>
        ReadTodoAsync(string connectionString, long id)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, title, done, created_at, updated_at FROM todos WHERE id = $1",
            conn);
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"todo {id} not found");
        return (
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetBoolean(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    private static async Task<long> CountTodosAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM todos", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Polls until two non-holder backends are lock-waiting in a chain rooted at the fixture holder.
    /// </summary>
    private static async Task WaitUntilBothApiSessionsLockWaitAsync(
        string connectionString,
        int holderPid,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();

        while (DateTime.UtcNow < deadline)
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT a.pid, pg_blocking_pids(a.pid) AS blockers
                FROM pg_stat_activity a
                WHERE a.datname = current_database()
                  AND a.pid <> pg_backend_pid()
                  AND a.pid <> @holder
                  AND a.state = 'active'
                  AND cardinality(pg_blocking_pids(a.pid)) > 0
                """,
                observer);
            cmd.Parameters.AddWithValue("holder", holderPid);

            var waiters = new List<(int Pid, int[] Blockers)>();
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var pid = reader.GetInt32(0);
                    var blockers = reader.GetFieldValue<int[]>(1);
                    waiters.Add((pid, blockers));
                }
            }

            var rooted = waiters
                .Where(w => IsLockWaitRootedAt(w.Pid, holderPid, waiters))
                .Select(w => w.Pid)
                .Distinct()
                .ToList();

            if (rooted.Count >= 2)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Timed out waiting for two API sessions lock-waiting behind holder pid {holderPid}.");
    }

    private static bool IsLockWaitRootedAt(
        int pid,
        int holderPid,
        IReadOnlyList<(int Pid, int[] Blockers)> waiters)
    {
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(pid);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            var entry = waiters.FirstOrDefault(w => w.Pid == current);
            if (entry.Pid == 0 && current != pid)
            {
                // Blocker may not itself be waiting (e.g. the holder).
                if (current == holderPid)
                {
                    return true;
                }

                continue;
            }

            foreach (var blocker in entry.Blockers ?? Array.Empty<int>())
            {
                if (blocker == holderPid)
                {
                    return true;
                }

                queue.Enqueue(blocker);
            }
        }

        return false;
    }

    private static DateTimeOffset ParseInstant(string? value)
    {
        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.True(DateTimeOffset.TryParse(value, out var parsed), $"timestamp not parseable: {value}");
        return parsed.ToUniversalTime();
    }

    private static string NormalizeInstant(string? value) =>
        ParseInstant(value).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
}
