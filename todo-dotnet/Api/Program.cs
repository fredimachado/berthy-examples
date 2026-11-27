using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

if (args is ["migrate"])
{
    var migrateConfig = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();
    return await MigrateCommand.RunAsync(migrateConfig, CancellationToken.None);
}

if (args is ["plant-migration-conflict"])
{
    var conflictConfig = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();
    return await MigrateCommand.PlantConflictAsync(conflictConfig, CancellationToken.None);
}

var builder = WebApplication.CreateBuilder(args);

// Go's ServeMux treats /api/todos and /api/todos/ as different paths.
builder.Services.Configure<RouteOptions>(options =>
{
    options.AppendTrailingSlash = false;
});

var hostName = ResolveHostName(builder.Configuration);
var build = BuildInfo.ReadBuildInfo(builder.Configuration, hostName);

var tododb = builder.Configuration.GetConnectionString("tododb");
if (!string.IsNullOrWhiteSpace(tododb))
{
    builder.Services.AddDbContext<TodoDbContext>(options => options.UseNpgsql(tododb));
}

var redis = builder.Configuration.GetConnectionString("redis");
if (!string.IsNullOrWhiteSpace(redis))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    {
        var options = ConfigurationOptions.Parse(redis);
        options.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(options);
    });
}

builder.Services.AddSingleton<TodoListCache>(sp =>
{
    var mux = sp.GetService<IConnectionMultiplexer>();
    return new TodoListCache(mux, sp.GetRequiredService<ILogger<TodoListCache>>());
});

var app = builder.Build();

if (build.Replica is { Length: > 0 } replica)
{
    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Served-By"] = replica;
        await next();
    });
}

// Go's ServeMux is case-sensitive and does not treat /api/todos/ as /api/todos.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value;
    if (path is { Length: > 1 } && path.EndsWith('/'))
    {
        await JsonError.Write(StatusCodes.Status404NotFound, "not found").ExecuteAsync(context);
        return;
    }

    if (path is not null
        && path.Equals("/api/todos", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(path, "/api/todos", StringComparison.Ordinal))
    {
        await JsonError.Write(StatusCodes.Status404NotFound, "not found").ExecuteAsync(context);
        return;
    }

    // /api/todos/{id} must keep the exact lowercase prefix (Go ServeMux is case-sensitive).
    if (path is not null
        && path.StartsWith("/api/todos/", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith("/api/todos/", StringComparison.Ordinal))
    {
        await JsonError.Write(StatusCodes.Status404NotFound, "not found").ExecuteAsync(context);
        return;
    }

    if (path is not null
        && path.Equals("/version", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(path, "/version", StringComparison.Ordinal))
    {
        await JsonError.Write(StatusCodes.Status404NotFound, "not found").ExecuteAsync(context);
        return;
    }

    if (path is not null
        && path.Equals("/health", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(path, "/health", StringComparison.Ordinal))
    {
        await JsonError.Write(StatusCodes.Status404NotFound, "not found").ExecuteAsync(context);
        return;
    }

    await next();
});

app.MapGet("/version", () => Results.Json(build, AppJson.Options));

if (!string.IsNullOrWhiteSpace(tododb))
{
    app.MapGet("/api/todos", async (TodoDbContext db, TodoListCache cache, CancellationToken ct) =>
    {
        var result = await cache.ListAsync(db, ct);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result.Items, AppJson.Options);
        return new CachedListResult(bytes, result.CacheHit);
    });

    app.MapPost("/api/todos", TodoCommands.CreateTodoAsync);
    app.MapMethods("/api/todos/{id}", ["PATCH"], TodoCommands.PatchTodoAsync);
    app.MapDelete("/api/todos/{id}", TodoCommands.DeleteTodoAsync);
}

// Always register /health so a missing tododb still reports postgres as failed.
app.MapGet("/health", async (IServiceProvider sp, CancellationToken ct) =>
{
    var probes = new DependencyProbe[]
    {
        new("postgres", async token =>
        {
            // Own a scope/context through query completion so request disposal cannot cut it short.
            await using var scope = sp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetService<TodoDbContext>()
                ?? throw new InvalidOperationException("postgres is not configured");
            await db.Database.ExecuteSqlRawAsync("SELECT 1", token);
        }),
        new("redis", async token =>
        {
            // ConnectAsync inside the probe so a cold sync Connect cannot starve postgres or the deadline.
            var redisCs = sp.GetRequiredService<IConfiguration>().GetConnectionString("redis");
            if (string.IsNullOrWhiteSpace(redisCs))
            {
                throw new InvalidOperationException("redis is not configured");
            }

            var options = ConfigurationOptions.Parse(redisCs);
            options.AbortOnConnectFail = false;
            await using var mux = await ConnectionMultiplexer.ConnectAsync(options);
            token.ThrowIfCancellationRequested();
            await mux.GetDatabase().PingAsync();
            token.ThrowIfCancellationRequested();
        }),
    };

    var failed = await DependencyHealth.FailedDependenciesAsync(probes, TimeProvider.System, ct);
    if (failed.Length == 0)
    {
        return Results.Json(new HealthOkResponse("ok"), AppJson.Options);
    }

    return Results.Json(
        new HealthUnavailableResponse("unavailable", failed),
        AppJson.Options,
        statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapFallback(() => JsonError.Write(StatusCodes.Status404NotFound, "not found"));

app.Run();
return 0;

static string? ResolveHostName(IConfiguration configuration)
{
    // Containers and tests supply HOSTNAME. An empty value omits the replica.
    if (configuration["HOSTNAME"] is { } configured)
    {
        return configured;
    }

    try
    {
        return Dns.GetHostName();
    }
    catch (Exception)
    {
        return null;
    }
}

file static class AppJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

file sealed class CachedListResult(byte[] bytes, bool cacheHit) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json";
        response.Headers["X-Cache"] = cacheHit ? "hit" : "miss";
        await response.Body.WriteAsync(bytes);
    }
}

file sealed record HealthOkResponse(string Status);
file sealed record HealthUnavailableResponse(string Status, string[] Failed);

public partial class Program;
