using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace Todo.Tests;

[Collection(ServiceCollection.Name)]
public sealed class ListTests
{
    private readonly ServiceFixture _services;

    public ListTests(ServiceFixture services) => _services = services;

    [Fact]
    public async Task ListReturnsRowsInIdOrderWithUtcTimestamps()
    {
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        var migrate = await MigrateProcess.RunAsync(database.ConnectionString);
        Assert.Equal(0, migrate.ExitCode);

        long firstId;
        long secondId;
        await using (var conn = new NpgsqlConnection(database.ConnectionString))
        {
            await conn.OpenAsync();
            await using (var first = new NpgsqlCommand(
                "INSERT INTO todos (title) VALUES ('alpha') RETURNING id",
                conn))
            {
                firstId = (long)(await first.ExecuteScalarAsync())!;
            }

            await using (var second = new NpgsqlCommand(
                "INSERT INTO todos (title, done) VALUES ('beta', true) RETURNING id",
                conn))
            {
                secondId = (long)(await second.ExecuteScalarAsync())!;
            }
        }

        Assert.True(firstId < secondId);

        await using var factory = new ApiFactory(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:tododb"] = database.ConnectionString,
                ["ConnectionStrings:redis"] = _services.RedisConnectionString,
                ["BuildVersion"] = "v-test",
                ["BuildCommit"] = "list-test",
                ["HOSTNAME"] = "",
            });
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/todos");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());

        var row0 = doc.RootElement[0];
        var row1 = doc.RootElement[1];
        Assert.Equal(firstId, row0.GetProperty("id").GetInt64());
        Assert.Equal("alpha", row0.GetProperty("title").GetString());
        Assert.False(row0.GetProperty("done").GetBoolean());
        Assert.Equal(secondId, row1.GetProperty("id").GetInt64());
        Assert.Equal("beta", row1.GetProperty("title").GetString());
        Assert.True(row1.GetProperty("done").GetBoolean());

        AssertUtcInstant(row0.GetProperty("created_at").GetString());
        AssertUtcInstant(row0.GetProperty("updated_at").GetString());
        AssertUtcInstant(row1.GetProperty("created_at").GetString());
        AssertUtcInstant(row1.GetProperty("updated_at").GetString());
    }

    private static void AssertUtcInstant(string? value)
    {
        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.True(
            DateTimeOffset.TryParse(value, out var parsed),
            $"timestamp not parseable: {value}");
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }
}
