using System.Text.Json.Serialization;

namespace swiftlyS2_countryflags.Models;

public sealed class PlayerData
{
    [JsonPropertyName("show")]
    public bool ShowFlag { get; set; }

    [JsonPropertyName("country")]
    public string CountryCode { get; set; } = string.Empty;

    [JsonPropertyName("lastFetch")]
    public DateTime LastFetch { get; set; }

    // Runtime only - not saved to JSON
    [JsonIgnore]
    public int AppliedBadgeId { get; set; }
}
