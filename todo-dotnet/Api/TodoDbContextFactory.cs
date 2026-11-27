using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

internal sealed class TodoDbContextFactory : IDesignTimeDbContextFactory<TodoDbContext>
{
    public TodoDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TodoDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=tododb;Username=todo;Password=design")
            .Options;
        return new TodoDbContext(options);
    }
}
