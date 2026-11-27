using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

internal sealed record TodoListResult(TodoResponse[] Items, bool CacheHit);

/// <summary>
/// Redis list cache for <c>todo:list</c>. Uses a short-lived fill token so a slow
/// miss cannot restore a value after a newer successful invalidation.
/// </summary>
internal sealed class TodoListCache
{
    internal const string Key = "todo:list";
    internal static readonly TimeSpan ListTtl = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan TokenTtl = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan InvalidationTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IConnectionMultiplexer? _mux;
    private readonly ILogger<TodoListCache> _logger;

    public TodoListCache(IConnectionMultiplexer? mux, ILogger<TodoListCache> logger)
    {
        _mux = mux;
        _logger = logger;
    }

    /// <summary>
    /// Test hook: runs after the Postgres read on a miss, before the CAS fill.
    /// </summary>
    internal Func<CancellationToken, Task>? AfterDatabaseReadAsync { get; set; }

    internal async Task<TodoListResult> ListAsync(TodoDbContext db, CancellationToken ct)
    {
        var redis = TryDatabase();
        RedisValue? corrupt = null;

        if (redis is not null)
        {
            try
            {
                var cached = await redis.StringGetAsync(Key).WaitAsync(ct);
                if (cached.HasValue)
                {
                    if (TryParseList(cached, out var items))
                    {
                        return new TodoListResult(items, CacheHit: true);
                    }

                    // Unreadable / token / corrupt: treat as miss and CAS-replace with a token.
                    corrupt = cached;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "cache read failed");
                redis = null;
            }
        }

        string? token = null;
        if (redis is not null)
        {
            token = "tok:" + Guid.NewGuid().ToString("N");
            try
            {
                if (corrupt is { } old)
                {
                    var tran = redis.CreateTransaction();
                    tran.AddCondition(Condition.StringEqual(Key, old));
                    _ = tran.StringSetAsync(Key, token, TokenTtl);
                    if (!await tran.ExecuteAsync().WaitAsync(ct))
                    {
                        token = null;
                    }
                }
                else
                {
                    var reserved = await redis.StringSetAsync(Key, token, TokenTtl, When.NotExists)
                        .WaitAsync(ct);
                    if (!reserved)
                    {
                        token = null;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "cache reserve failed");
                token = null;
            }
        }

        var fromDb = await TodoQueries.ReadTodosAsync(db, ct);

        if (AfterDatabaseReadAsync is not null)
        {
            await AfterDatabaseReadAsync(ct);
        }

        if (redis is not null && token is not null)
        {
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(fromDb, JsonOptions);
                var tran = redis.CreateTransaction();
                tran.AddCondition(Condition.StringEqual(Key, token));
                _ = tran.StringSetAsync(Key, payload, ListTtl);
                _ = await tran.ExecuteAsync().WaitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "cache fill failed");
            }
        }

        return new TodoListResult(fromDb, CacheHit: false);
    }

    /// <summary>
    /// Best-effort delete of the list key or fill token. Bounded and independent of
    /// client disconnect so a committed write still attempts invalidation.
    /// </summary>
    internal async Task InvalidateAsync(CancellationToken ct)
    {
        var redis = TryDatabase();
        if (redis is null)
        {
            return;
        }

        try
        {
            using var bound = new CancellationTokenSource(InvalidationTimeout);
            await redis.KeyDeleteAsync(Key).WaitAsync(bound.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "cache invalidation failed");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("cache invalidation timed out");
        }
    }

    private IDatabase? TryDatabase()
    {
        if (_mux is null)
        {
            return null;
        }

        try
        {
            return _mux.GetDatabase();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "cache unavailable");
            return null;
        }
    }

    private static bool TryParseList(RedisValue value, out TodoResponse[] items)
    {
        items = [];
        if (!value.HasValue)
        {
            return false;
        }

        var bytes = (byte[])value!;
        // Fill tokens are not JSON arrays.
        if (bytes.Length == 0 || bytes[0] != (byte)'[')
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<TodoResponse[]>(bytes, JsonOptions);
            items = parsed ?? [];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
