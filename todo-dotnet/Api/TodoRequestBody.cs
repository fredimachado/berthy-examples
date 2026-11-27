using System.Text.Json;
using System.Text.Json.Serialization;

internal static class TodoRequestBody
{
    internal const int MaxBodyBytes = 8192;

    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

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
                var parsed = JsonSerializer.Deserialize<CreateTodoBody>(ref reader, StrictOptions);
                if (parsed is null)
                {
                    return (null, JsonError.Write(StatusCodes.Status400BadRequest, "invalid JSON: expected an object"));
                }

                body = parsed;
            }
            else
            {
                return (null, JsonError.Write(
                    StatusCodes.Status400BadRequest,
                    "invalid JSON: expected an object or null"));
            }

            if (reader.Read())
            {
                return (null, JsonError.Write(
                    StatusCodes.Status400BadRequest,
                    "invalid JSON: unexpected data after the JSON object"));
            }

            return (body, null);
        }
        catch (JsonException ex)
        {
            return (null, JsonError.Write(
                StatusCodes.Status400BadRequest,
                "invalid JSON: " + JsonProblem(ex)));
        }
    }

    private static string JsonProblem(JsonException ex)
    {
        if (ex.Message.Contains("not been mapped", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Unmapped member", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("extensible", StringComparison.OrdinalIgnoreCase))
        {
            return ex.Message;
        }

        if (ex.Path is { Length: > 0 })
        {
            var field = ex.Path.TrimStart('$', '.');
            if (field.Length > 0)
            {
                return $"field \"{field}\" has the wrong type";
            }
        }

        return "syntax error";
    }
}
