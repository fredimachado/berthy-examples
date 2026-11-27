using Microsoft.EntityFrameworkCore;

internal static class TodoQueries
{
    internal static Task<TodoResponse[]> ReadTodosAsync(TodoDbContext db, CancellationToken ct) =>
        db.Todos
            .AsNoTracking()
            .OrderBy(t => t.Id)
            .Select(t => new TodoResponse(t.Id, t.Title, t.Done, t.CreatedAt, t.UpdatedAt))
            .ToArrayAsync(ct);
}
