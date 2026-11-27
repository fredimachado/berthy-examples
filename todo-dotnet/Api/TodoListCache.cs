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
    internal const string TokenPrefix = "tok:";
    internal static readonly TimeSpan ListTtl = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan TokenTtl = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan InvalidationTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Cached payloads must match the real list shape; reject [{}] and null titles as hits.
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    private readonly AppRedis _redis;
    private readonly ILogger<TodoListCache> _logger;

    public TodoListCache(AppRedis redis, ILogger<TodoListCache> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// Test hook: runs after the Postgres read on a miss, before the CAS fill.
    /// </summary>
    internal Func<CancellationToken, Task>? AfterDatabaseReadAsync { get; set; }

    /// <summary>
    /// Test hook: runs at the start of invalidation Redis work (before connect/delete).
    /// </summary>
    internal Func<Task>? BeforeInvalidateRedisAsync { get; set; }

    internal async Task<TodoListResult> ListAsync(TodoDbContext db, CancellationToken ct)
    {
        var redis = await TryDatabaseAsync(ct);
        RedisValue? corrupt = null;
        var liveToken = false;

        if (redis is not null)
        {
            try
            {
                var cached = await redis.StringGetAsync(Key).WaitAsync(ct);
                if (cached.HasValue)
                {
                    if (IsFillToken(cached))
                    {
                        // Another reader reserved the key; leave the token alone.
                        liveToken = true;
                    }
                    else if (TryParseList(cached, out var items))
                    {
                        return new TodoListResult(items, CacheHit: true);
                    }
                    else
                    {
                        // Unreadable / corrupt: CAS-replace with a token.
                        corrupt = cached;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "cache read failed");
                redis = null;
            }
        }

        string? token = null;
        if (redis is not null && !liveToken)
        {
            token = TokenPrefix + Guid.NewGuid().ToString("N");
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
    /// Best-effort delete of the list key or fill token. Uses an independent ~2s budget so
    /// client disconnect after commit cannot skip invalidation. <paramref name="ct"/> is ignored.
    /// </summary>
    internal async Task InvalidateAsync(CancellationToken ct)
    {
        _ = ct; // Request cancellation must not abort post-commit invalidation.

        if (BeforeInvalidateRedisAsync is not null)
        {
            await BeforeInvalidateRedisAsync();
        }

        // One independent token for connect and delete — not linked to the request.
        using var bound = new CancellationTokenSource(InvalidationTimeout);
        IDatabase? redis;
        try
        {
            redis = await TryDatabaseAsync(bound.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "cache unavailable");
            return;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("cache invalidation timed out");
            return;
        }

        if (redis is null)
        {
            return;
        }

        try
        {
            await redis.KeyDeleteAsync(Key).WaitAsync(bound.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "cache invalidation failed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("cache invalidation timed out");
        }
    }

    private async Task<IDatabase?> TryDatabaseAsync(CancellationToken ct)
    {
        if (!_redis.IsConfigured)
        {
            return null;
        }

        try
        {
            var mux = await _redis.GetMultiplexerAsync(ct);
            return mux.GetDatabase();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "cache unavailable");
            return null;
        }
    }

    /// <summary>
    /// True only for a live reservation: <c>tok:</c> plus a 32-char GUID "N" form.
    /// Values like <c>tok:broken</c> are corrupt and must be repaired.
    /// </summary>
    internal static bool IsFillToken(RedisValue value)
    {
        if (!value.HasValue)
        {
            return false;
        }

        var text = (string?)value;
        return text is not null
            && text.StartsWith(TokenPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(text.AsSpan(TokenPrefix.Length), "N", out _);
    }

    private static bool TryParseList(RedisValue value, out TodoResponse[] items)
    {
        items = [];
        if (!value.HasValue)
        {
            return false;
        }

        var bytes = (byte[])value!;
        if (bytes.Length == 0 || bytes[0] != (byte)'[')
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<TodoResponse?[]>(bytes, JsonOptions);
            if (parsed is null)
            {
                return false;
            }

            // Reject malformed hits such as [null] — treat as corrupt and refill.
            for (var i = 0; i < parsed.Length; i++)
            {
                if (parsed[i] is null)
                {
                    return false;
                }
            }

            items = parsed!;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
