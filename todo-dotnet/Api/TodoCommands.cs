using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

internal static class TodoCommands
{
    private static readonly JsonSerializerOptions ResponseOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static async Task<IResult> CreateTodoAsync(
        HttpRequest request,
        TodoDbContext db,
        TodoListCache cache,
        CancellationToken ct)
    {
        var (body, error) = await TodoRequestBody.ReadCreateAsync(request, ct);
        if (error is not null)
        {
            return error;
        }

        if (!TodoTitle.TryParse(body!.Title, out var title))
        {
            return JsonError.Write(
                StatusCodes.Status422UnprocessableEntity,
                "title must be 1 to 200 characters");
        }

        try
        {
            // Only the title is written; done/timestamps come from database defaults (RETURNING).
            var row = new TodoRow { Title = title!.Value };
            db.Todos.Add(row);
            await db.SaveChangesAsync(ct);

            await cache.InvalidateAsync(ct);

            var response = new TodoResponse(row.Id, row.Title, row.Done, row.CreatedAt, row.UpdatedAt);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response, ResponseOptions);
            return new RawJsonResult(
                StatusCodes.Status201Created,
                bytes,
                location: $"/api/todos/{row.Id}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var logger = request.HttpContext.RequestServices
                .GetService<ILoggerFactory>()
                ?.CreateLogger("Todo.Create");
            logger?.LogError(ex, "request failed {Method} {Path}", request.Method, request.Path);
            return JsonError.Write(StatusCodes.Status500InternalServerError, "internal error");
        }
    }

    internal static async Task<IResult> PatchTodoAsync(
        string id,
        HttpRequest request,
        TodoDbContext db,
        TodoListCache cache,
        CancellationToken ct)
    {
        if (!TryParsePositiveId(id, out var todoId))
        {
            return JsonError.Write(StatusCodes.Status400BadRequest, "id must be a positive number");
        }

        var (body, error) = await TodoRequestBody.ReadPatchAsync(request, ct);
        if (error is not null)
        {
            return error;
        }

        if (body!.Title is null && body.Done is null)
        {
            return JsonError.Write(
                StatusCodes.Status422UnprocessableEntity,
                "give a title, done or both");
        }

        string? cleanTitle = null;
        if (body.Title is not null)
        {
            if (!TodoTitle.TryParse(body.Title, out var title))
            {
                return JsonError.Write(
                    StatusCodes.Status422UnprocessableEntity,
                    "title must be 1 to 200 characters");
            }

            cleanTitle = title!.Value;
        }

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Lock the row before reading values used for the change (lost-update protection).
            var locked = await db.Todos
                .FromSql($"SELECT id, title, done, created_at, updated_at FROM todos WHERE id = {todoId} FOR UPDATE")
                .AsTracking()
                .SingleOrDefaultAsync(ct);

            if (locked is null)
            {
                await tx.RollbackAsync(ct);
                return JsonError.Write(StatusCodes.Status404NotFound, "todo not found");
            }

            if (cleanTitle is not null)
            {
                locked.Title = cleanTitle;
            }

            if (body.Done is { } done)
            {
                locked.Done = done;
            }

            // timestamptz is microsecond-precision; match so response equals a fresh SELECT.
            locked.UpdatedAt = TruncateToMicroseconds(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            await cache.InvalidateAsync(ct);

            var response = new TodoResponse(
                locked.Id,
                locked.Title,
                locked.Done,
                locked.CreatedAt,
                locked.UpdatedAt);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response, ResponseOptions);
            return new RawJsonResult(StatusCodes.Status200OK, bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var logger = request.HttpContext.RequestServices
                .GetService<ILoggerFactory>()
                ?.CreateLogger("Todo.Patch");
            logger?.LogError(ex, "request failed {Method} {Path}", request.Method, request.Path);
            return JsonError.Write(StatusCodes.Status500InternalServerError, "internal error");
        }
    }

    internal static async Task<IResult> DeleteTodoAsync(
        string id,
        HttpRequest request,
        TodoDbContext db,
        TodoListCache cache,
        CancellationToken ct)
    {
        if (!TryParsePositiveId(id, out var todoId))
        {
            return JsonError.Write(StatusCodes.Status400BadRequest, "id must be a positive number");
        }

        try
        {
            var affected = await db.Todos.Where(t => t.Id == todoId).ExecuteDeleteAsync(ct);
            if (affected == 0)
            {
                return JsonError.Write(StatusCodes.Status404NotFound, "todo not found");
            }

            await cache.InvalidateAsync(ct);

            return Results.NoContent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var logger = request.HttpContext.RequestServices
                .GetService<ILoggerFactory>()
                ?.CreateLogger("Todo.Delete");
            logger?.LogError(ex, "request failed {Method} {Path}", request.Method, request.Path);
            return JsonError.Write(StatusCodes.Status500InternalServerError, "internal error");
        }
    }

    /// <summary>
    /// Matches Go <c>strconv.ParseInt(s, 10, 64)</c>: optional leading sign and ASCII digits only.
    /// Rejects whitespace padding (e.g. path segment <c>%2042</c>).
    /// </summary>
    internal static bool TryParsePositiveId(string id, out long value)
    {
        value = 0;
        if (id.Length == 0)
        {
            return false;
        }

        var i = 0;
        if (id[0] is '+' or '-')
        {
            i = 1;
            if (id.Length == 1)
            {
                return false;
            }
        }

        for (; i < id.Length; i++)
        {
            if (id[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)
            && value >= 1;
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var ticks = utc.Ticks - (utc.Ticks % 10);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
