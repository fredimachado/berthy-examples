using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

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

    await next();
});

app.MapGet("/version", () => Results.Json(build, AppJson.Options));

if (!string.IsNullOrWhiteSpace(tododb))
{
    app.MapGet("/api/todos", async (TodoDbContext db, CancellationToken ct) =>
    {
        var todos = await TodoQueries.ReadTodosAsync(db, ct);
        return Results.Json(todos, AppJson.Options);
    });

    app.MapPost("/api/todos", TodoCommands.CreateTodoAsync);
    app.MapMethods("/api/todos/{id}", ["PATCH"], TodoCommands.PatchTodoAsync);
    app.MapDelete("/api/todos/{id}", TodoCommands.DeleteTodoAsync);
}

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

public partial class Program;
