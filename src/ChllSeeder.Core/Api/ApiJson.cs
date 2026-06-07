using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChllSeeder.Core.Api;

/// <summary>Shared System.Text.Json options for the seeding API: snake_case
/// property names and lowercase string enums, matching the Rust serde wire format.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        // AuthProvider serializes as "steam"/"discord"/"guest".
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }
}
