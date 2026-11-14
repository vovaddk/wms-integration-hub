using System.Text.Json;

namespace IntegrationHub.Core.Mapping;

internal static class MappingJson
{
    public static T Deserialize<T>(string payload, JsonSerializerOptions options)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, options) ?? throw new MappingException("Payload is empty.");
        }
        catch (JsonException ex)
        {
            throw new MappingException($"Payload does not match the expected format: {ex.Message}", ex);
        }
    }

    public static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new MappingException($"Field '{field}' is required.") : value.Trim();

    public static IReadOnlyList<T> NotEmpty<T>(IReadOnlyList<T>? items, string field) =>
        items is { Count: > 0 } ? items : throw new MappingException($"Field '{field}' must contain at least one line.");
}
