using System.Text.Json.Serialization;

internal sealed record CreateTodoBody(
    [property: JsonPropertyName("title")] string? Title);
