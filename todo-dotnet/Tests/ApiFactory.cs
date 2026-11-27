using Microsoft.AspNetCore.Mvc.Testing;

namespace Todo.Tests;

internal sealed class ApiFactory : WebApplicationFactory<Program>
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
