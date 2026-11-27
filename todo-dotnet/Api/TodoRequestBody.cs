using System.Text.Json;

internal static class TodoRequestBody
{
    internal const int MaxBodyBytes = 8192;

    /// <summary>
    /// Reads at most <see cref="MaxBodyBytes"/> + 1 bytes (even without Content-Length).
    /// Accepts one JSON object or the literal null, then optional whitespace.
    /// Top-level null supplies an empty input record.
    /// </summary>
    internal static async Task<(CreateTodoBody? Body, IResult? Error)> ReadCreateAsync(
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
            return (null, JsonError.Write(StatusCodes.Status413RequestEntityTooLarge, "request body too large"));
        }

        if (total == 0)
        {
            return (null, JsonError.Write(StatusCodes.Status400BadRequest, "invalid JSON: the body is empty"));
        }

        try
        {
            var reader = new Utf8JsonReader(buffer.AsSpan(0, total));
            if (!reader.Read())
            {
                return (null, JsonError.Write(StatusCodes.Status400BadRequest, "invalid JSON: the body is empty"));
            }

            CreateTodoBody body;
            if (reader.TokenType == JsonTokenType.Null)
            {
                body = new CreateTodoBody(null);
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                var (parsed, error) = ReadCreateObject(ref reader);
                if (error is not null)
                {
                    return (null, error);
                }

                body = parsed!;
            }
            else
            {
                return (null, JsonError.Write(
                    StatusCodes.Status400BadRequest,
                    "invalid JSON: expected an object or null"));
            }

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
        catch (JsonException)
        {
            return (null, JsonError.Write(
                StatusCodes.Status400BadRequest,
                "invalid JSON: syntax error"));
        }
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
            var fieldName = reader.GetString() ?? "";

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
                    $"invalid JSON: json: unknown field \"{fieldName}\""));
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    title = null;
                    break;
                case JsonTokenType.String:
                    title = reader.GetString();
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
}
