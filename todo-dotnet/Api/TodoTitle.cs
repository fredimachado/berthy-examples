using System.Text;

internal sealed class TodoTitle
{
    private const int MaxLength = 200;

    internal const char ReplacementChar = '\uFFFD';

    private TodoTitle(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? raw, out TodoTitle? title)
    {
        title = null;
        if (raw is null)
        {
            return false;
        }

        // Match Go encoding/json + utf8.RuneCount: unpaired surrogates become U+FFFD first.
        var normalized = ReplaceUnpairedSurrogates(raw);
        var trimmed = normalized.Trim();
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

    internal static string ReplaceUnpairedSurrogates(string value)
    {
        var needsFix = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (!char.IsSurrogate(c))
            {
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
                continue;
            }

            needsFix = true;
            break;
        }

        if (!needsFix)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    sb.Append(c);
                    sb.Append(value[i + 1]);
                    i++;
                }
                else
                {
                    sb.Append(ReplacementChar);
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                sb.Append(ReplacementChar);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
