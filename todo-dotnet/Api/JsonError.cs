using System.Text.Json;

internal static class JsonError
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
    };

    internal static IResult Write(int statusCode, string message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, string> { ["error"] = message },
            Options);
        return new RawJsonResult(statusCode, bytes);
    }
}

internal sealed class RawJsonResult(int statusCode, byte[] bytes, string? location = null) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        if (location is not null)
        {
            response.Headers.Location = location;
        }

        await response.Body.WriteAsync(bytes);
    }
}
