using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Todo.Tests;

[Collection(ServiceCollection.Name)]
public sealed class PublishTests
{
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromMinutes(5);
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string ApiProject = Path.Combine(RepoRoot, "todo-dotnet", "Api", "Todo.Api.csproj");
    private static readonly string WebDir = Path.Combine(RepoRoot, "todo-dotnet", "web");
    private static readonly string ReadmePath = Path.Combine(RepoRoot, "todo-dotnet", "README.md");
    private static readonly string RootReadmePath = Path.Combine(RepoRoot, "README.md");
    private static readonly string WorkflowPath = Path.Combine(RepoRoot, ".github", "workflows", "todo-dotnet.yml");
    private static readonly string VerifyScript = Path.Combine(RepoRoot, "todo-dotnet", "scripts", "verify.ps1");

    private readonly ServiceFixture _services;

    public PublishTests(ServiceFixture services) => _services = services;

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

    [Fact]
    public async Task PublishedAppServesItsOwnFrontend()
    {
        using var cts = new CancellationTokenSource(PublishTimeout);
        var publishDir = Path.Combine(Path.GetTempPath(), "todo-pub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(publishDir);

        Process? apiProcess = null;
        try
        {
            var publish = await RunProcessAsync(
                "dotnet",
                ["publish", ApiProject, "--configuration", "Release", "--output", publishDir],
                RepoRoot,
                cts.Token);
            Assert.True(
                publish.ExitCode == 0,
                $"dotnet publish failed ({publish.ExitCode}): {publish.StdErr}\n{publish.StdOut}");

            Assert.True(
                File.Exists(Path.Combine(publishDir, "wwwroot", "index.html")),
                "Published output must include wwwroot/index.html from the Vite build.");

            await using var database = await IsolatedDatabase.CreateAsync(_services.PostgresConnectionString);
            var migrate = await RunDllAsync(
                publishDir,
                ["migrate"],
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings__tododb"] = database.ConnectionString,
                    ["ASPNETCORE_URLS"] = "",
                },
                cts.Token);
            Assert.Equal(0, migrate.ExitCode);

            var listen = StartListeningDll(
                publishDir,
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings__tododb"] = database.ConnectionString,
                    ["ConnectionStrings__redis"] = _services.RedisConnectionString,
                    ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
                    ["HOSTNAME"] = "published-replica",
                    ["BuildVersion"] = "v-published",
                    ["BuildCommit"] = "published-commit",
                });
            apiProcess = listen.Process;
            var baseUrl = await listen.UrlTask.WaitAsync(cts.Token);
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

            using (var root = await client.GetAsync("/", cts.Token))
            {
                Assert.Equal(HttpStatusCode.OK, root.StatusCode);
                Assert.Equal("text/html", root.Content.Headers.ContentType?.MediaType);
                Assert.Equal("published-replica", root.Headers.GetValues("X-Served-By").Single());
                var html = await root.Content.ReadAsStringAsync(cts.Token);
                Assert.Contains("<div id=\"root\">", html, StringComparison.Ordinal);

                var assetUrls = Regex.Matches(html, """(?:src|href)="(/assets/[^"]+)""")
                    .Select(m => m.Groups[1].Value)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                Assert.NotEmpty(assetUrls);

                foreach (var assetUrl in assetUrls)
                {
                    using var asset = await client.GetAsync(assetUrl, cts.Token);
                    Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
                    var mediaType = asset.Content.Headers.ContentType?.MediaType;
                    Assert.False(string.IsNullOrWhiteSpace(mediaType));
                    Assert.True(
                        mediaType!.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                        || mediaType.Contains("css", StringComparison.OrdinalIgnoreCase)
                        || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                        || mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase),
                        $"Unexpected asset content type '{mediaType}' for {assetUrl}");
                }
            }

            var hadTodoBaseUrl = Environment.GetEnvironmentVariable("TODO_BASE_URL") is not null;
            var previousTodoBaseUrl = Environment.GetEnvironmentVariable("TODO_BASE_URL");
            try
            {
                Environment.SetEnvironmentVariable("TODO_BASE_URL", baseUrl.TrimEnd('/'));
                var e2e = await RunProcessAsync(
                    "npm",
                    ["--prefix", WebDir, "run", "test:e2e"],
                    RepoRoot,
                    cts.Token);
                Assert.True(
                    e2e.ExitCode == 0,
                    $"Playwright against published app failed ({e2e.ExitCode}): {e2e.StdErr}\n{e2e.StdOut}");
            }
            finally
            {
                if (hadTodoBaseUrl)
                {
                    Environment.SetEnvironmentVariable("TODO_BASE_URL", previousTodoBaseUrl);
                }
                else
                {
                    Environment.SetEnvironmentVariable("TODO_BASE_URL", null);
                }
            }
        }
        finally
        {
            if (apiProcess is not null && !apiProcess.HasExited)
            {
                apiProcess.Kill(entireProcessTree: true);
                await apiProcess.WaitForExitAsync();
            }

            apiProcess?.Dispose();
            TryDeleteDirectory(publishDir);
        }
    }

    [Fact]
    public void CleanCheckoutWalkthrough()
    {
        Assert.True(File.Exists(ReadmePath), "todo-dotnet/README.md must exist.");
        Assert.True(File.Exists(WorkflowPath), ".github/workflows/todo-dotnet.yml must exist.");
        Assert.True(File.Exists(VerifyScript), "todo-dotnet/scripts/verify.ps1 must exist.");

        var readme = File.ReadAllText(ReadmePath);
        Assert.Contains("aspire start", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verify.ps1", readme, StringComparison.Ordinal);
        Assert.Contains("AspireWeb", readme, StringComparison.Ordinal);
        Assert.Contains("PublishedWeb", readme, StringComparison.Ordinal);
        Assert.Contains("dotnet publish", readme, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings:tododb", readme, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings:redis", readme, StringComparison.Ordinal);
        Assert.Contains("todo:list", readme, StringComparison.Ordinal);
        Assert.Contains("X-Cache", readme, StringComparison.Ordinal);

        var rootReadme = File.ReadAllText(RootReadmePath);
        Assert.Contains("[`todo-dotnet/`](todo-dotnet/README.md)", rootReadme, StringComparison.Ordinal);
        Assert.Contains("Aspire", rootReadme, StringComparison.Ordinal);

        var workflow = File.ReadAllText(WorkflowPath);
        Assert.Contains("dotnet test todo-dotnet/Tests/Todo.Tests.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("PublishedWeb", workflow, StringComparison.Ordinal);
        Assert.Contains("AspireWeb", workflow, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/checkout@", workflow, StringComparison.Ordinal);

        var verify = File.ReadAllText(VerifyScript);
        Assert.Contains("PublishedWeb", verify, StringComparison.Ordinal);
        Assert.Contains("PublishDir", verify, StringComparison.Ordinal);
    }

    private static (Process Process, Task<string> UrlTask) StartListeningDll(
        string publishDir,
        IReadOnlyDictionary<string, string?> environment)
    {
        var dll = Path.Combine(publishDir, "Todo.Api.dll");
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = publishDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(dll);
        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        var buffer = new StringBuilder();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };

        void OnData(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            buffer.AppendLine(e.Data);
            var match = Regex.Match(e.Data, @"Now listening on:\s*(https?://\S+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                tcs.TrySetResult(match.Groups[1].Value.TrimEnd('/'));
            }
        }

        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;
        process.Exited += (_, _) =>
        {
            if (!tcs.Task.IsCompleted)
            {
                tcs.TrySetException(new InvalidOperationException(
                    $"Published API exited before listening (code {process.ExitCode}). Output:{Environment.NewLine}{buffer}"));
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start published Todo.Api.dll.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return (process, tcs.Task);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunDllAsync(
        string publishDir,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var dll = Path.Combine(publishDir, "Todo.Api.dll");
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = publishDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(dll);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        using var process = new Process { StartInfo = start };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start published Todo.Api.dll.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = start };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {fileName}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort temp cleanup.
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
