using StackExchange.Redis;

/// <summary>
/// Application-owned Redis multiplexer. One lazy ConnectAsync shared by cache and health.
/// </summary>
internal sealed class AppRedis
{
    private readonly string? _connectionString;
    private readonly object _gate = new();
    private Task<IConnectionMultiplexer>? _connect;

    internal AppRedis(string? connectionString) => _connectionString = connectionString;

    internal bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    internal Task<IConnectionMultiplexer> GetMultiplexerAsync(CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("redis is not configured");
        }

        Task<IConnectionMultiplexer> connect;
        lock (_gate)
        {
            connect = _connect ??= ConnectCoreAsync();
        }

        // Apply the caller's deadline to the wait without starting a second connect.
        return connect.WaitAsync(ct);
    }

    private async Task<IConnectionMultiplexer> ConnectCoreAsync()
    {
        var options = ConfigurationOptions.Parse(_connectionString!);
        options.AbortOnConnectFail = false;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }
}
