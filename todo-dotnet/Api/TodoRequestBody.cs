using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

internal static class TodoRequestBody
{
    internal const int MaxBodyBytes = 8192;

    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        // Go's encoding/json matches field names case-insensitively.
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly Regex UnmappedPropertyName = new(
        "['\"](?<name>[^'\"]+)['\"] could not be mapped|JSON property '(?<name>[^']+)'",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
        catch (JsonException ex)
        {
            return (null, JsonError.Write(
                StatusCodes.Status400BadRequest,
                "invalid JSON: " + JsonProblem(ex)));
        }
    }

    private static string JsonProblem(JsonException ex)
    {
        if (IsUnmappedMember(ex))
        {
            var name = ExtractUnmappedName(ex) ?? "unknown";
            return $"json: unknown field \"{name}\"";
        }

        if (ex.Path is { Length: > 0 }
            && (ex.Message.Contains("could not be converted", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("wrong type", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("Boolean", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("Number", StringComparison.OrdinalIgnoreCase)))
        {
            var field = ex.Path.TrimStart('$', '.');
            if (field.Length > 0)
            {
                return $"field \"{field}\" has the wrong type";
            }
        }

        return "syntax error";
    }

    private static bool IsUnmappedMember(JsonException ex) =>
        ex.Message.Contains("not been mapped", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("could not be mapped", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Unmapped member", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("extensible", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("cannot be mapped", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractUnmappedName(JsonException ex)
    {
        var match = UnmappedPropertyName.Match(ex.Message);
        if (match.Success)
        {
            return match.Groups["name"].Value;
        }

        if (ex.Path is { Length: > 0 })
        {
            return ex.Path.TrimStart('$', '.');
        }

        return null;
    }
}
