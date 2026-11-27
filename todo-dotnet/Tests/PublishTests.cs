using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Todo.Tests;

public sealed class PublishStaticTests
{
    [Fact]
    public async Task UnknownRoutesDoNotServeTheSpa()
    {
        using var spa = SpaWebRoot.CreateMinimal();
        await using var factory = new ApiFactory(
            new Dictionary<string, string?>
            {
                ["HOSTNAME"] = "replica-spa",
            },
            spa.Path);
        var client = factory.CreateClient();

        using (var root = await client.GetAsync("/"))
        {
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
            Assert.Equal("text/html", root.Content.Headers.ContentType?.MediaType);
            var html = await root.Content.ReadAsStringAsync();
            Assert.Contains("Todo", html, StringComparison.Ordinal);
            Assert.DoesNotContain("\"error\"", html, StringComparison.Ordinal);
        }

        foreach (var path in new[] { "/api/unknown", "/nope" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(
                new[] { "application/json" },
                response.Content.Headers.GetValues("Content-Type").ToArray());
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("not found", doc.RootElement.GetProperty("error").GetString());
            Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
        }

        using (var missing = await client.GetAsync("/assets/does-not-exist-abc123.js"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var body = await missing.Content.ReadAsStringAsync();
            Assert.DoesNotContain("<!doctype html", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed class PublishDocsTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string ReadmePath = Path.Combine(RepoRoot, "todo-dotnet", "README.md");
    private static readonly string RootReadmePath = Path.Combine(RepoRoot, "README.md");
    private static readonly string WorkflowPath = Path.Combine(RepoRoot, ".github", "workflows", "todo-dotnet.yml");
    private static readonly string VerifyScript = Path.Combine(RepoRoot, "todo-dotnet", "scripts", "verify.ps1");

    [Fact]
    public void CleanCheckoutWalkthrough()
    {
        Assert.True(File.Exists(ReadmePath), "todo-dotnet/README.md must exist.");
        Assert.True(File.Exists(WorkflowPath), ".github/workflows/todo-dotnet.yml must exist.");
        Assert.True(File.Exists(VerifyScript), "todo-dotnet/scripts/verify.ps1 must exist.");

        var rootReadme = File.ReadAllText(RootReadmePath);
        var linkMatch = Regex.Match(
            rootReadme,
            @"\[`todo-dotnet/`\]\((?<dest>[^)]+)\)",
            RegexOptions.CultureInvariant);
        Assert.True(linkMatch.Success, "Root README must link to the todo-dotnet example.");
        var dest = linkMatch.Groups["dest"].Value.Replace('/', Path.DirectorySeparatorChar);
        var linkedPath = Path.GetFullPath(Path.Combine(RepoRoot, dest));
        Assert.True(
            File.Exists(linkedPath),
            $"Root README todo-dotnet link destination missing: {linkedPath}");
        Assert.Equal(Path.GetFullPath(ReadmePath), linkedPath);
    }
}

[Collection(ServiceCollection.Name)]
public sealed class PublishTests
{
    private readonly ServiceFixture _services;

    public PublishTests(ServiceFixture services) => _services = services;

    [Fact]
    public async Task ReplicaHeaderCoversPublishedRootAndErrors()
    {
        using var spa = SpaWebRoot.CreateMinimal();
        await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
        Assert.Equal(0, (await MigrateProcess.RunAsync(database.ConnectionString)).ExitCode);

        await using var factory = new ApiFactory(
            new Dictionary<string, string?>
            {
                ["BuildVersion"] = "v-pub",
                ["BuildCommit"] = "pub-commit",
                ["HOSTNAME"] = "replica-published",
                ["ConnectionStrings:tododb"] = database.ConnectionString,
                ["ConnectionStrings:redis"] = _services.RedisConnectionString,
            },
            spa.Path);
        var client = factory.CreateClient();

        using var version = await client.GetAsync("/version");
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        using var versionDoc = await JsonDocument.ParseAsync(await version.Content.ReadAsStreamAsync());
        var expectedReplica = versionDoc.RootElement.GetProperty("replica").GetString();
        Assert.Equal("replica-published", expectedReplica);
        Assert.Equal(expectedReplica, version.Headers.GetValues("X-Served-By").Single());

        using (var root = await client.GetAsync("/"))
        {
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
            Assert.Equal("text/html", root.Content.Headers.ContentType?.MediaType);
            Assert.Equal(expectedReplica, root.Headers.GetValues("X-Served-By").Single());
        }

        foreach (var path in new[] { "/version", "/health", "/api/todos" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(
                expectedReplica,
                response.Headers.GetValues("X-Served-By").Single());
        }

        using (var unknown = await client.GetAsync("/nope"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal(expectedReplica, unknown.Headers.GetValues("X-Served-By").Single());
        }

        using (var invalidPost = await client.PostAsync(
            "/api/todos",
            new StringContent("{}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidPost.StatusCode);
            Assert.Equal(expectedReplica, invalidPost.Headers.GetValues("X-Served-By").Single());
        }

        await using var emptyIdentity = new ApiFactory(
            new Dictionary<string, string?>
            {
                ["BuildVersion"] = "v-pub",
                ["BuildCommit"] = "pub-commit",
                ["HOSTNAME"] = "",
                ["ConnectionStrings:tododb"] = database.ConnectionString,
                ["ConnectionStrings:redis"] = _services.RedisConnectionString,
            },
            spa.Path);
        var emptyClient = emptyIdentity.CreateClient();

        using (var versionEmpty = await emptyClient.GetAsync("/version"))
        {
            Assert.False(versionEmpty.Headers.Contains("X-Served-By"));
            using var doc = await JsonDocument.ParseAsync(await versionEmpty.Content.ReadAsStreamAsync());
            Assert.False(doc.RootElement.TryGetProperty("replica", out _));
        }

        foreach (var path in new[] { "/", "/health", "/api/todos", "/nope" })
        {
            using var response = await emptyClient.GetAsync(path);
            Assert.False(response.Headers.Contains("X-Served-By"));
        }
    }
}

internal sealed class SpaWebRoot : IDisposable
{
    private SpaWebRoot(string path) => Path = path;

    public string Path { get; }

    public static SpaWebRoot CreateMinimal()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "todo-spa-" + Guid.NewGuid().ToString("N"));
        var assets = System.IO.Path.Combine(root, "assets");
        Directory.CreateDirectory(assets);
        File.WriteAllText(
            System.IO.Path.Combine(root, "index.html"),
            """
            <!doctype html>
            <html lang="en">
            <head><meta charset="UTF-8" /><title>Todo</title></head>
            <body><div id="root"></div><script type="module" src="/assets/app.js"></script></body>
            </html>
            """);
        File.WriteAllText(System.IO.Path.Combine(assets, "app.js"), "console.log('spa');");
        return new SpaWebRoot(root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort temp cleanup.
        }
    }
}
