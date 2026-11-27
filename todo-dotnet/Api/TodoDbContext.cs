using Microsoft.EntityFrameworkCore;

internal sealed class TodoDbContext(DbContextOptions<TodoDbContext> options) : DbContext(options)
{
    internal DbSet<TodoRow> Todos => Set<TodoRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var todos = modelBuilder.Entity<TodoRow>();
        todos.ToTable("todos", table =>
        {
            table.HasCheckConstraint(
                "todos_title_length",
                "char_length(title) BETWEEN 1 AND 200");
        });

        todos.HasKey(t => t.Id);
        todos.Property(t => t.Id)
            .HasColumnName("id")
            .UseIdentityAlwaysColumn();

        todos.Property(t => t.Title)
            .HasColumnName("title")
            .HasColumnType("text")
            .IsRequired();

        todos.Property(t => t.Done)
            .HasColumnName("done")
            .IsRequired()
            .HasDefaultValue(false);

        todos.Property(t => t.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired()
            .HasDefaultValueSql("now()");

        todos.Property(t => t.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired()
            .HasDefaultValueSql("now()");
    }
}
