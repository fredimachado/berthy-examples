using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Todo.Tests;

[Collection(ServiceCollection.Name)]
public sealed class CreateTests
{
    private readonly ServiceFixture _services;

    public CreateTests(ServiceFixture services) => _services = services;

    [Fact]
    public async Task PostCreatesTrimmedTodoAndLocation()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        await using var factory = CreateFactory(database.ConnectionString, replica: "");
        var client = factory.CreateClient();

        foreach (var withContentType in new[] { true, false })
        {
            using var content = new ByteArrayContent(
                Encoding.UTF8.GetBytes("""{"title":"  buy milk  "}"""));
            if (withContentType)
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            using var response = await client.PostAsync("/api/todos", content);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetInt64();
            Assert.True(id > 0);
            Assert.Equal("buy milk", root.GetProperty("title").GetString());
            Assert.False(root.GetProperty("done").GetBoolean());
            AssertUtcInstant(root.GetProperty("created_at").GetString());
            AssertUtcInstant(root.GetProperty("updated_at").GetString());

            Assert.True(response.Headers.TryGetValues("Location", out var locations));
            Assert.Equal($"/api/todos/{id}", locations.Single());
        }

        using var list = await client.GetAsync("/api/todos");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        await using var listStream = await list.Content.ReadAsStreamAsync();
        using var listDoc = await JsonDocument.ParseAsync(listStream);
        Assert.Equal(2, listDoc.RootElement.GetArrayLength());
        Assert.Equal("buy milk", listDoc.RootElement[0].GetProperty("title").GetString());
        Assert.Equal("buy milk", listDoc.RootElement[1].GetProperty("title").GetString());
    }

    [Fact]
    public async Task TitleCountsUnicodeScalarsAfterTrimming()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        await using var factory = CreateFactory(database.ConnectionString, replica: "");
        var client = factory.CreateClient();

        // One scalar (supplementary plane), padded with Unicode whitespace.
        using (var one = await PostJsonAsync(client, "{\"title\":\" \u2003\U0001F600\u2003 \"}"))
        {
            Assert.Equal(HttpStatusCode.Created, one.StatusCode);
            using var doc = await JsonDocument.ParseAsync(await one.Content.ReadAsStreamAsync());
            Assert.Equal("\U0001F600", doc.RootElement.GetProperty("title").GetString());
        }

        var twoHundred = string.Concat(Enumerable.Repeat("\U0001F600", 200));
        using (var ok = await PostJsonAsync(client, "{\"title\":\"  " + twoHundred + "  \"}"))
        {
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
            using var doc = await JsonDocument.ParseAsync(await ok.Content.ReadAsStreamAsync());
            Assert.Equal(twoHundred, doc.RootElement.GetProperty("title").GetString());
        }

        var twoHundredOne = string.Concat(Enumerable.Repeat("\U0001F600", 201));
        using (var tooLong = await PostJsonAsync(client, "{\"title\":\"" + twoHundredOne + "\"}"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
            await AssertExactJsonErrorAsync(tooLong, "title must be 1 to 200 characters");
        }

        using (var blank = await PostJsonAsync(client, "{\"title\":\" \u2003\u00A0 \"}"))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
            await AssertExactJsonErrorAsync(blank, "title must be 1 to 200 characters");
        }

        Assert.Equal(2, await CountTodosAsync(database.ConnectionString));
    }

    [Fact]
    public async Task InvalidBodiesHaveStableStatusAndDoNotWrite()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        await using var factory = CreateFactory(database.ConnectionString, replica: "replica-create");
        var client = factory.CreateClient();

        var cases = new (string Name, string Body, HttpStatusCode Status, string? ExactError)[]
        {
            ("empty", "", HttpStatusCode.BadRequest, null),
            ("malformed", "title=x", HttpStatusCode.BadRequest, null),
            ("wrong type", """{"title":5}""", HttpStatusCode.BadRequest, null),
            ("unknown field", """{"title":"x","id":9}""", HttpStatusCode.BadRequest,
                "invalid JSON: json: unknown field \"id\""),
            ("unknown field apostrophe", "{\"title\":\"x\",\"owner's\":1}", HttpStatusCode.BadRequest,
                "invalid JSON: json: unknown field \"owner's\""),
            ("unknown field quoted", "{\"title\":\"x\",\"a\\\"b\\\\c\":1}", HttpStatusCode.BadRequest,
                "invalid JSON: json: unknown field \"a\\\"b\\\\c\""),
            ("trailing object", """{"title":"x"} {}""", HttpStatusCode.BadRequest,
                "invalid JSON: unexpected data after the JSON object"),
            ("empty object", "{}", HttpStatusCode.UnprocessableEntity,
                "title must be 1 to 200 characters"),
            ("null title", """{"title":null}""", HttpStatusCode.UnprocessableEntity,
                "title must be 1 to 200 characters"),
            ("literal null", "null", HttpStatusCode.UnprocessableEntity,
                "title must be 1 to 200 characters"),
        };

        foreach (var tc in cases)
        {
            using var response = await PostJsonAsync(client, tc.Body);
            Assert.True(
                response.StatusCode == tc.Status,
                $"{tc.Name}: status={(int)response.StatusCode}, want={(int)tc.Status}");
            Assert.Equal(
                new[] { "application/json" },
                response.Content.Headers.GetValues("Content-Type").ToArray());
            Assert.Equal("replica-create", response.Headers.GetValues("X-Served-By").Single());

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var error = doc.RootElement.GetProperty("error").GetString();
            Assert.False(string.IsNullOrEmpty(error));
            if (tc.ExactError is not null)
            {
                Assert.Equal(tc.ExactError, error);
            }
        }

        Assert.Equal(0, await CountTodosAsync(database.ConnectionString));
    }

    [Fact]
    public async Task BodyLimitAppliesWithoutContentLength()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        await using var factory = CreateFactory(database.ConnectionString, replica: "");
        var client = factory.CreateClient();

        var exact = BuildPaddedTitleBody(8192);
        using (var ok = await client.PostAsync("/api/todos", new ChunkedBytesContent(exact)))
        {
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        var over = BuildPaddedTitleBody(8193);
        using (var tooBig = await client.PostAsync("/api/todos", new ChunkedBytesContent(over)))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
            Assert.Equal(
                new[] { "application/json" },
                tooBig.Content.Headers.GetValues("Content-Type").ToArray());
            await AssertExactJsonErrorAsync(tooBig, "request body too large");
        }

        Assert.Equal(1, await CountTodosAsync(database.ConnectionString));
    }

    [Fact]
    public async Task DatabaseErrorsHideConnectionDetails()
    {
        // Point at a closed local port so the driver fails without a live server.
        var broken =
            "Host=127.0.0.1;Port=1;Database=tododb;Username=todo;Password=secret-should-not-leak";

        await using var factory = CreateFactory(broken, replica: "replica-db-error");
        var client = factory.CreateClient();

        using var response = await PostJsonAsync(client, """{"title":"x"}""");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            new[] { "application/json" },
            response.Content.Headers.GetValues("Content-Type").ToArray());
        Assert.Equal("replica-db-error", response.Headers.GetValues("X-Served-By").Single());

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("internal error", doc.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("secret-should-not-leak", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Connection", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrailingSlashPostDoesNotCreate()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        await using var factory = CreateFactory(database.ConnectionString, replica: "");
        // Do not follow redirects: a slash redirect must not become a successful create.
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await PostJsonAsync(client, """{"title":"slash"}""", path: "/api/todos/");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertExactJsonErrorAsync(response, "not found");
        Assert.Equal(0, await CountTodosAsync(database.ConnectionString));
    }

    [Fact]
    public async Task UppercasePathPostDoesNotCreate()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        await using var factory = CreateFactory(database.ConnectionString, replica: "");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await PostJsonAsync(client, """{"title":"upper-path"}""", path: "/API/TODOS");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertExactJsonErrorAsync(response, "not found");
        Assert.Equal(0, await CountTodosAsync(database.ConnectionString));
    }

    [Fact]
    public async Task MixedCaseTitleFieldIsAcceptedAndUnknownRejected()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        await using var factory = CreateFactory(database.ConnectionString, replica: "");
        var client = factory.CreateClient();

        using (var ok = await PostJsonAsync(client, """{"Title":"  Mixed  "}"""))
        {
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
            using var doc = await JsonDocument.ParseAsync(await ok.Content.ReadAsStreamAsync());
            Assert.Equal("Mixed", doc.RootElement.GetProperty("title").GetString());
        }

        using (var upper = await PostJsonAsync(client, """{"TITLE":"upper"}"""))
        {
            Assert.Equal(HttpStatusCode.Created, upper.StatusCode);
        }

        using (var unknown = await PostJsonAsync(client, """{"title":"x","id":9}"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            await AssertExactJsonErrorAsync(unknown, "invalid JSON: json: unknown field \"id\"");
        }

        Assert.Equal(2, await CountTodosAsync(database.ConnectionString));
    }

    [Fact]
    public async Task UnpairedSurrogatesBecomeReplacementChars()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        await using var factory = CreateFactory(database.ConnectionString, replica: "");
        var client = factory.CreateClient();

        // Go encoding/json replaces unpaired UTF-16 surrogates with U+FFFD before validation.
        using var response = await PostJsonAsync(client, "{\"title\":\"\\uD800x\\uDFFF\"}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var title = doc.RootElement.GetProperty("title").GetString();
        Assert.Equal("\uFFFDx\uFFFD", title);
        Assert.Equal(1, await CountTodosAsync(database.ConnectionString));
    }

    [Fact]
    public async Task LaterNullTitleDoesNotClearEarlierValue()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        await using var factory = CreateFactory(database.ConnectionString, replica: "");
        var client = factory.CreateClient();

        // Go: JSON null into a string has no effect, so Title:null must not wipe "buy milk".
        using var response = await PostJsonAsync(client, """{"title":"buy milk","Title":null}""");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal("buy milk", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(1, await CountTodosAsync(database.ConnectionString));
    }

    [Fact]
    public async Task PostAndGetShareDatabaseTimestamps()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        await using var factory = CreateFactory(database.ConnectionString, replica: "");
        var client = factory.CreateClient();

        using var created = await PostJsonAsync(client, """{"title":"ts"}""");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDoc = await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync());
        var id = createdDoc.RootElement.GetProperty("id").GetInt64();
        var createdAt = createdDoc.RootElement.GetProperty("created_at").GetString();
        var updatedAt = createdDoc.RootElement.GetProperty("updated_at").GetString();
        Assert.False(string.IsNullOrWhiteSpace(createdAt));
        Assert.Equal(createdAt, updatedAt);

        using var list = await client.GetAsync("/api/todos");
        using var listDoc = await JsonDocument.ParseAsync(await list.Content.ReadAsStreamAsync());
        var row = listDoc.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetInt64() == id);
        Assert.Equal(createdAt, row.GetProperty("created_at").GetString());
        Assert.Equal(updatedAt, row.GetProperty("updated_at").GetString());
    }

    private ApiFactory CreateFactory(string connectionString, string replica) =>
        new(new Dictionary<string, string?>
        {
            ["ConnectionStrings:tododb"] = connectionString,
            ["ConnectionStrings:redis"] = _services.RedisConnectionString,
            ["BuildVersion"] = "v-test",
            ["BuildCommit"] = "create-test",
            ["HOSTNAME"] = replica,
        });

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string body,
        string path = "/api/todos")
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await client.PostAsync(path, content);
    }

    private static async Task AssertExactJsonErrorAsync(HttpResponseMessage response, string message)
    {
        Assert.Equal(
            new[] { "application/json" },
            response.Content.Headers.GetValues("Content-Type").ToArray());
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(message, doc.RootElement.GetProperty("error").GetString());
    }

    private static async Task<long> CountTodosAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM todos", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static byte[] BuildPaddedTitleBody(int totalBytes)
    {
        const string prefix = "{\"title\":\"";
        const string suffix = "\"}";
        var pad = totalBytes - prefix.Length - suffix.Length;
        Assert.True(pad >= 8, $"body size {totalBytes} is too small for a padded title");
        // Spaces around a short title so trimming still yields a valid 1..200 title.
        var title = "  buy  " + new string(' ', pad - 7);
        var body = prefix + title + suffix;
        Assert.Equal(totalBytes, Encoding.UTF8.GetByteCount(body));
        return Encoding.UTF8.GetBytes(body);
    }

    private static void AssertUtcInstant(string? value)
    {
        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.True(DateTimeOffset.TryParse(value, out var parsed), $"timestamp not parseable: {value}");
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    /// <summary>
    /// Forces chunked transfer so Content-Length is absent.
    /// </summary>
    private sealed class ChunkedBytesContent : HttpContent
    {
        private readonly byte[] _bytes;

        public ChunkedBytesContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
