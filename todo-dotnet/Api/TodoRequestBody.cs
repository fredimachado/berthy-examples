using System.Buffers;
using System.Text;
using System.Text.Json;

internal static class TodoRequestBody
{
    internal const int MaxBodyBytes = 8192;

    private delegate (T? Body, IResult? Error) ObjectReader<T>(ref Utf8JsonReader reader);

    /// <summary>
    /// Reads at most <see cref="MaxBodyBytes"/> + 1 bytes (even without Content-Length).
    /// Accepts one JSON object or the literal null, then optional whitespace.
    /// Top-level null supplies an empty input record.
    /// </summary>
    internal static Task<(CreateTodoBody? Body, IResult? Error)> ReadCreateAsync(
        HttpRequest request,
        CancellationToken ct) =>
        ReadTopLevelAsync(request, ReadCreateObject, static () => new CreateTodoBody(null), ct);

    /// <summary>
    /// Same top-level body rules as create. Field rules differ: omitted/null members stay null.
    /// </summary>
    internal static Task<(PatchTodoBody? Body, IResult? Error)> ReadPatchAsync(
        HttpRequest request,
        CancellationToken ct) =>
        ReadTopLevelAsync(request, ReadPatchObject, static () => new PatchTodoBody(null, null), ct);

    /// <summary>
    /// Shared object-or-null envelope for create and patch; <paramref name="readObject"/> owns fields.
    /// </summary>
    private static async Task<(T? Body, IResult? Error)> ReadTopLevelAsync<T>(
        HttpRequest request,
        ObjectReader<T> readObject,
        Func<T> emptyFromNull,
        CancellationToken ct)
        where T : class
    {
        var (buffer, total, error) = await ReadBodyBytesAsync(request, ct);
        if (error is not null)
        {
            return (null, error);
        }

        try
        {
            var reader = new Utf8JsonReader(buffer.AsSpan(0, total));
            if (!reader.Read())
            {
                return (null, JsonError.Write(StatusCodes.Status400BadRequest, "invalid JSON: the body is empty"));
            }

            T body;
            if (reader.TokenType == JsonTokenType.Null)
            {
                body = emptyFromNull();
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                var (parsed, parseError) = readObject(ref reader);
                if (parseError is not null)
                {
                    return (null, parseError);
                }

                body = parsed!;
            }
            else
            {
                return (null, JsonError.Write(
                    StatusCodes.Status400BadRequest,
                    "invalid JSON: expected an object or null"));
            }

            return FinishObjectOrNull(ref reader, body);
        }
        catch (JsonException)
        {
            return (null, JsonError.Write(
                StatusCodes.Status400BadRequest,
                "invalid JSON: syntax error"));
        }
    }

    private static async Task<(byte[] Buffer, int Total, IResult? Error)> ReadBodyBytesAsync(
        HttpRequest request,
        CancellationToken ct)
    {
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > MaxBodyBytes)
        {
            return (buffer, total, JsonError.Write(
                StatusCodes.Status413RequestEntityTooLarge,
                "request body too large"));
        }

        if (total == 0)
        {
            return (buffer, total, JsonError.Write(
                StatusCodes.Status400BadRequest,
                "invalid JSON: the body is empty"));
        }

        return (buffer, total, null);
    }

    private static (T? Body, IResult? Error) FinishObjectOrNull<T>(ref Utf8JsonReader reader, T body)
        where T : class
    {
        try
        {
            if (reader.Read())
            {
                return (null, JsonError.Write(
                    StatusCodes.Status400BadRequest,
                    "invalid JSON: unexpected data after the JSON object"));
            }
        }
        catch (JsonException)
        {
            // Trailing junk that fails to tokenize still matches Go's trailing-data rejection.
            return (null, JsonError.Write(
                StatusCodes.Status400BadRequest,
                "invalid JSON: unexpected data after the JSON object"));
        }

        return (body, null);
    }

    /// <summary>
    /// Walks object tokens so unknown field names come from the JSON property token
    /// (including names with apostrophes), matching Go's DisallowUnknownFields.
    /// Title matching is case-insensitive like encoding/json.
    /// </summary>
    private static (CreateTodoBody? Body, IResult? Error) ReadCreateObject(ref Utf8JsonReader reader)
    {
        string? title = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return (new CreateTodoBody(title), null);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                return (null, JsonError.Write(
                    StatusCodes.Status400BadRequest,
                    "invalid JSON: syntax error"));
            }

            // Exact wire name from the token — do not parse exception text.
            var fieldName = ReadJsonStringLikeGo(ref reader);

            if (!reader.Read())
            {
                return (null, JsonError.Write(
                    StatusCodes.Status400BadRequest,
                    "invalid JSON: syntax error"));
            }

            if (!fieldName.Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                return (null, JsonError.Write(
                    StatusCodes.Status400BadRequest,
                    "invalid JSON: json: unknown field " + GoQuote(fieldName)));
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    // Go: JSON null into a string has no effect (keeps a prior value).
                    break;
                case JsonTokenType.String:
                    title = ReadJsonStringLikeGo(ref reader);
                    break;
                default:
                    return (null, JsonError.Write(
                        StatusCodes.Status400BadRequest,
                        "invalid JSON: field \"title\" has the wrong type"));
            }
        }

        return (null, JsonError.Write(
            StatusCodes.Status400BadRequest,
            "invalid JSON: syntax error"));
    }

    private static (PatchTodoBody? Body, IResult? Error) ReadPatchObject(ref Utf8JsonReader reader)
    {
        string? title = null;
        bool? done = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return (new PatchTodoBody(title, done), null);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                return (null, JsonError.Write(
                    StatusCodes.Status400BadRequest,
                    "invalid JSON: syntax error"));
            }

            var fieldName = ReadJsonStringLikeGo(ref reader);

            if (!reader.Read())
            {
                return (null, JsonError.Write(
                    StatusCodes.Status400BadRequest,
                    "invalid JSON: syntax error"));
            }

            if (fieldName.Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.Null:
                        // JSON null into *string → leave field alone (nil).
                        title = null;
                        break;
                    case JsonTokenType.String:
                        title = ReadJsonStringLikeGo(ref reader);
                        break;
                    default:
                        return (null, JsonError.Write(
                            StatusCodes.Status400BadRequest,
                            "invalid JSON: field \"title\" has the wrong type"));
                }

                continue;
            }

            if (fieldName.Equals("done", StringComparison.OrdinalIgnoreCase))
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.Null:
                        done = null;
                        break;
                    case JsonTokenType.True:
                        done = true;
                        break;
                    case JsonTokenType.False:
                        done = false;
                        break;
                    default:
                        return (null, JsonError.Write(
                            StatusCodes.Status400BadRequest,
                            "invalid JSON: field \"done\" has the wrong type"));
                }

                continue;
            }

            return (null, JsonError.Write(
                StatusCodes.Status400BadRequest,
                "invalid JSON: json: unknown field " + GoQuote(fieldName)));
        }

        return (null, JsonError.Write(
            StatusCodes.Status400BadRequest,
            "invalid JSON: syntax error"));
    }

    /// <summary>
    /// Decodes a JSON string token like Go encoding/json: unpaired UTF-16 surrogates
    /// become U+FFFD.
    /// </summary>
    private static string ReadJsonStringLikeGo(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.PropertyName))
        {
            throw new InvalidOperationException("ReadJsonStringLikeGo requires a string token.");
        }

        if (!reader.ValueIsEscaped)
        {
            return TodoTitle.ReplaceUnpairedSurrogates(Encoding.UTF8.GetString(reader.ValueSpan));
        }

        return UnescapeJsonString(reader.ValueSpan);
    }

    private static string UnescapeJsonString(ReadOnlySpan<byte> escapedUtf8)
    {
        var sb = new StringBuilder(escapedUtf8.Length);
        for (var i = 0; i < escapedUtf8.Length;)
        {
            var b = escapedUtf8[i];
            if (b != (byte)'\\')
            {
                // Unescaped JSON string bytes are UTF-8.
                if (b < 0x80)
                {
                    sb.Append((char)b);
                    i++;
                    continue;
                }

                var status = Rune.DecodeFromUtf8(escapedUtf8[i..], out var rune, out var consumed);
                if (status != OperationStatus.Done || consumed == 0)
                {
                    sb.Append(TodoTitle.ReplacementChar);
                    i++;
                    continue;
                }

                sb.Append(rune.ToString());
                i += consumed;
                continue;
            }

            if (i + 1 >= escapedUtf8.Length)
            {
                sb.Append(TodoTitle.ReplacementChar);
                break;
            }

            var esc = (char)escapedUtf8[i + 1];
            i += 2;
            switch (esc)
            {
                case '"':
                case '\\':
                case '/':
                    sb.Append(esc);
                    break;
                case 'b':
                    sb.Append('\b');
                    break;
                case 'f':
                    sb.Append('\f');
                    break;
                case 'n':
                    sb.Append('\n');
                    break;
                case 'r':
                    sb.Append('\r');
                    break;
                case 't':
                    sb.Append('\t');
                    break;
                case 'u':
                    if (i + 4 > escapedUtf8.Length
                        || !TryParseHex4(escapedUtf8.Slice(i, 4), out var unit))
                    {
                        sb.Append(TodoTitle.ReplacementChar);
                        break;
                    }

                    i += 4;
                    if (char.IsHighSurrogate((char)unit))
                    {
                        if (i + 6 <= escapedUtf8.Length
                            && escapedUtf8[i] == (byte)'\\'
                            && escapedUtf8[i + 1] == (byte)'u'
                            && TryParseHex4(escapedUtf8.Slice(i + 2, 4), out var low)
                            && char.IsLowSurrogate((char)low))
                        {
                            sb.Append((char)unit);
                            sb.Append((char)low);
                            i += 6;
                        }
                        else
                        {
                            sb.Append(TodoTitle.ReplacementChar);
                        }
                    }
                    else if (char.IsLowSurrogate((char)unit))
                    {
                        sb.Append(TodoTitle.ReplacementChar);
                    }
                    else
                    {
                        sb.Append((char)unit);
                    }

                    break;
                default:
                    sb.Append(TodoTitle.ReplacementChar);
                    break;
            }
        }

        return sb.ToString();
    }

    private static bool TryParseHex4(ReadOnlySpan<byte> hex, out ushort value)
    {
        value = 0;
        if (hex.Length != 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            var c = hex[i];
            int nibble;
            if (c is >= (byte)'0' and <= (byte)'9')
            {
                nibble = c - '0';
            }
            else if (c is >= (byte)'a' and <= (byte)'f')
            {
                nibble = c - 'a' + 10;
            }
            else if (c is >= (byte)'A' and <= (byte)'F')
            {
                nibble = c - 'A' + 10;
            }
            else
            {
                return false;
            }

            value = (ushort)((value << 4) | nibble);
        }

        return true;
    }

    /// <summary>
    /// Go <c>fmt %q</c> / <c>strconv.Quote</c> for unknown-field messages.
    /// </summary>
    internal static string GoQuote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20 || char.IsSurrogate(c))
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
