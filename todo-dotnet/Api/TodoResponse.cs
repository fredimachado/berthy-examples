using System.Text.Json.Serialization;

internal sealed record TodoResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);
