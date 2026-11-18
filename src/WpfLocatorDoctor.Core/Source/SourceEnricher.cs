using WpfLocatorDoctor.Core.Model;

namespace WpfLocatorDoctor.Core.Source;

/// <summary>
/// A live scan sees IDs but not where they came from. If the source code is available,
/// look each ID up in the XAML to learn whether it was set on purpose or borrowed from x:Name / x:Uid.
/// </summary>
public static class SourceEnricher
{
    /// <returns>How many live elements had their ID origin filled in.</returns>
    public static int Enrich(ScanResult live, ScanResult source)
    {
        var origins = source.Screens
            .SelectMany(s => s.Elements)
            .Where(e => e.AutomationId is not null && e.IdOrigin is not IdOrigin.DataBound and not IdOrigin.None)
            .GroupBy(e => e.AutomationId!, StringComparer.Ordinal)
            .Select(g => (Id: g.Key, Origins: g.Select(e => e.IdOrigin).Distinct().ToList()))
            .Where(x => x.Origins.Count == 1)
            .ToDictionary(x => x.Id, x => x.Origins[0], StringComparer.Ordinal);

        var updated = 0;
        foreach (var e in live.Screens.SelectMany(s => s.Elements))
        {
            if (e.IdOrigin != IdOrigin.Unknown || e.AutomationId is null) continue;
            if (origins.TryGetValue(e.AutomationId, out var origin))
            {
                e.IdOrigin = origin;
                updated++;
            }
        }

        live.Notes.Add($"Matched {updated} live IDs to the source code to learn where they come from.");
        return updated;
    }
}
