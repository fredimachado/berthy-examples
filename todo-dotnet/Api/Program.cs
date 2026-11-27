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

app.MapGet("/version", () => Results.Json(build, AppJson.Options));

if (!string.IsNullOrWhiteSpace(tododb))
{
    app.MapGet("/api/todos", async (TodoDbContext db, CancellationToken ct) =>
    {
        var todos = await TodoQueries.ReadTodosAsync(db, ct);
        return Results.Json(todos, AppJson.Options);
    });
}

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
