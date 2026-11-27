internal sealed class TodoTitle
{
    private const int MaxLength = 200;

    private TodoTitle(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? raw, out TodoTitle? title)
    {
        title = null;
        if (raw is null)
        {
            return false;
        }

        var trimmed = raw.Trim();
        var count = 0;
        foreach (var _ in trimmed.EnumerateRunes())
        {
            count++;
            if (count > MaxLength)
            {
                return false;
            }
        }

        if (count < 1)
        {
            return false;
        }

        title = new TodoTitle(trimmed);
        return true;
    }
}
