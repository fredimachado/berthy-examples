internal sealed class TodoRow
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public bool Done { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
