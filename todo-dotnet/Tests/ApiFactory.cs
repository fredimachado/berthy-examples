using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using StackExchange.Redis;

namespace Todo.Tests;

internal sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;
    private readonly string? _webRoot;

    public ApiFactory(Dictionary<string, string?> settings, string? webRoot = null)
    {
        _settings = settings;
        _webRoot = webRoot;
        // Collection tests share one Redis; clear the list key so Postgres isolation is not undone by a stale hit.
        if (settings.TryGetValue("ConnectionStrings:redis", out var redis)
            && !string.IsNullOrWhiteSpace(redis)
            && !redis.Contains("localhost:1", StringComparison.Ordinal))
        {
            try
            {
                using var mux = ConnectionMultiplexer.Connect(redis);
                mux.GetDatabase().KeyDelete("todo:list");
            }
            catch (Exception)
            {
                // Dead/unreachable Redis is covered by fallback tests.
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        if (_webRoot is not null)
        {
            builder.UseWebRoot(_webRoot);
        }
    }
}
