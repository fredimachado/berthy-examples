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
            var now = DateTimeOffset.UtcNow;
            var row = new TodoRow
            {
                Title = title!.Value,
                Done = false,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Todos.Add(row);
            await db.SaveChangesAsync(ct);

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
}
