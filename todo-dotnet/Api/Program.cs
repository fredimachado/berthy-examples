using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

var hostName = ResolveHostName(builder.Configuration);
var build = BuildInfo.ReadBuildInfo(builder.Configuration, hostName);

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

app.Run();

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
