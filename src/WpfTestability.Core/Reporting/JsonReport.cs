using System.Text.Json;
using System.Text.Json.Serialization;
using WpfTestability.Core.Scoring;

namespace WpfTestability.Core.Reporting;

/// <summary>Machine-readable report. Also used as the baseline for the next run's stability check.</summary>
public static class JsonReport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(Scorecard card) => JsonSerializer.Serialize(card, Options);

    public static Scorecard Deserialize(string json) =>
        JsonSerializer.Deserialize<Scorecard>(json, Options) ?? throw new InvalidDataException("The report file is empty.");

    public static void Save(Scorecard card, string path) => File.WriteAllText(path, Serialize(card));

    public static Scorecard Load(string path) => Deserialize(File.ReadAllText(path));
}
