using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Todo.Tests;

public sealed class VersionTests
{
    [Fact]
    public async Task VersionReportsBuildAndReplica()
    {
        await using var factory = new ApiFactory(
            new Dictionary<string, string?>
            {
                ["BuildVersion"] = "v1.2.3",
                ["BuildCommit"] = "abc1234",
                ["HOSTNAME"] = "replica-1",
            });
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("replica-1", response.Headers.GetValues("X-Served-By").Single());

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = doc.RootElement;
        Assert.Equal("v1.2.3", root.GetProperty("version").GetString());
        Assert.Equal("abc1234", root.GetProperty("commit").GetString());
        Assert.Equal("replica-1", root.GetProperty("replica").GetString());
    }

    [Fact]
    public async Task VersionHasLocalDefaults()
    {
        await using var factory = new ApiFactory(
            new Dictionary<string, string?>
            {
                ["BuildVersion"] = "",
                ["BuildCommit"] = "",
                ["HOSTNAME"] = "",
            });
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("X-Served-By", out _));

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = doc.RootElement;
        Assert.Equal("dev", root.GetProperty("version").GetString());
        Assert.Equal("unknown", root.GetProperty("commit").GetString());
        Assert.False(root.TryGetProperty("replica", out _));
    }

    [Fact]
    public async Task ReplicaHeaderCoversErrors()
    {
        await using var withReplica = new ApiFactory(
            new Dictionary<string, string?>
            {
                ["BuildVersion"] = "v1.2.3",
                ["BuildCommit"] = "abc1234",
                ["HOSTNAME"] = "replica-1",
            });
        using (var response = await withReplica.CreateClient().GetAsync("/nope"))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(new[] { "replica-1" }, response.Headers.GetValues("X-Served-By").ToArray());
        }

        await using var withoutReplica = new ApiFactory(
            new Dictionary<string, string?>
            {
                ["BuildVersion"] = "",
                ["BuildCommit"] = "",
                ["HOSTNAME"] = "",
            });
        using (var response = await withoutReplica.CreateClient().GetAsync("/nope"))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.False(response.Headers.Contains("X-Served-By"));
            Assert.Empty(response.Headers.TryGetValues("X-Served-By", out var values) ? values : []);
        }
    }
}

file sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;

    public ApiFactory(Dictionary<string, string?> settings) => _settings = settings;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
    }
}
