using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Demo.Models;
using Microsoft.EntityFrameworkCore;

namespace Demo.Data
{
    public static partial class WikidataStagingImporter
    {
        // Fetch many items from WDQS (paged) and insert as StagedRevolution rows.
        // Be polite: set a User-Agent with contact before calling or pass an HttpClient that has one.
        public static async Task<int> FetchAndStageAllAsync(RevolutionContext db, HttpClient? client = null, int limit = 250, CancellationToken ct = default)
        {
            if (db is null) throw new ArgumentNullException(nameof(db));
            if (limit <= 0) limit = 250;

            var createdClient = false;
            if (client == null)
            {
                client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
                {
                    Timeout = TimeSpan.FromSeconds(120)
                };
                createdClient = true;
            }

            try
            {
                try { client.DefaultRequestHeaders.UserAgent.ParseAdd("RevolutionsDataMap/0.1 (contact: dev@example.com)"); } catch { }
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

                static string ValueOf(JsonElement el, string name)
                    => el.TryGetProperty(name, out var prop) && prop.TryGetProperty("value", out var v) ? v.GetString() ?? string.Empty : string.Empty;

                // Use a SPARQL similar to the existing importer; page through results
                var sparql = @"SELECT DISTINCT ?item ?itemLabel ?itemDescription ?place ?placeLabel ?country ?countryLabel ?countryIso ?countryQid ?startDate ?end ?qid ?coord WHERE {
  OPTIONAL { ?item wdt:P276 ?place. ?place wdt:P17 ?countryFromPlace. }
  OPTIONAL { ?item wdt:P131 ?placeAdmin. ?placeAdmin wdt:P17 ?countryFromAdmin. }
  BIND(COALESCE(?countryFromPlace, ?countryFromAdmin) AS ?country)
  BIND(STRAFTER(STR(?country), 'http://www.wikidata.org/entity/') AS ?countryQid)
  OPTIONAL { ?country wdt:P297 ?countryIso. }
  OPTIONAL { ?country rdfs:label ?countryLabel FILTER(LANG(?countryLabel) = 'en') }
  FILTER(BOUND(?country))
  { ?item wdt:P31/wdt:P279* wd:Q10931. } UNION {
    ?item schema:description ?desc .
    FILTER(LANG(?desc) = 'en' &&
      (CONTAINS(LCASE(?desc),'revolution') ||
       CONTAINS(LCASE(?desc),'uprising') ||
       CONTAINS(LCASE(?desc),'rebellion') ||
       CONTAINS(LCASE(?desc),'insurgency') ||
       CONTAINS(LCASE(?desc),'coup') ||
       CONTAINS(LCASE(?desc),'protest')))
  }
  OPTIONAL { ?item wdt:P580 ?s. }
  OPTIONAL { ?item wdt:P585 ?p. }
  BIND(COALESCE(?s, ?p) AS ?startDate)
  FILTER(BOUND(?startDate) && YEAR(?startDate) >= 1900)
  OPTIONAL { ?item wdt:P582 ?end. }
  OPTIONAL { ?item schema:description ?itemDescription FILTER(LANG(?itemDescription) = 'en') }
  OPTIONAL { ?item wdt:P625 ?coord. }
  BIND(STRAFTER(STR(?item), 'http://www.wikidata.org/entity/') AS ?qid)
  SERVICE wikibase:label { bd:serviceParam wikibase:language ""[AUTO_LANGUAGE],en"". }
} ORDER BY ?countryLabel ?startDate ?qid LIMIT {limit} OFFSET {offset}";

                // Cache existing WikidataIds to avoid duplicates
                var existingWikidata = new HashSet<string>(
                    await db.Revolutions.Where(r => r.WikidataId != null).Select(r => r.WikidataId!).ToListAsync(ct),
                    StringComparer.OrdinalIgnoreCase);

                var stagedExisting = new HashSet<string>(
                    await db.StagedRevolutions.Where(s => s.WikidataId != null).Select(s => s.WikidataId!).ToListAsync(ct),
                    StringComparer.OrdinalIgnoreCase);

                var offset = 0;
                var totalStaged = 0;
                var batch = new List<StagedRevolution>(limit);

                while (!ct.IsCancellationRequested)
                {
                    var query = sparql.Replace("{limit}", limit.ToString(CultureInfo.InvariantCulture))
                                      .Replace("{offset}", offset.ToString(CultureInfo.InvariantCulture));

                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://query.wikidata.org/sparql")
                    {
                        Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("query", query) })
                    };
                    req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

                    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync(ct);
                        // include truncated body to avoid huge logs
                        var truncated = body?.Length > 2000 ? body.Substring(0, 2000) + "..." : body;
                        throw new HttpRequestException($"Wikidata SPARQL request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Response: {truncated}");
                    }

                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    if (!doc.RootElement.TryGetProperty("results", out var results) || !results.TryGetProperty("bindings", out var bindings))
                        break;

                    var bindingArray = bindings.EnumerateArray().ToArray();
                    if (bindingArray.Length == 0) break;

                    foreach (var b in bindingArray)
                    {
                        var qid = ValueOf(b, "qid");
                        if (string.IsNullOrWhiteSpace(qid)) continue;
                        if (existingWikidata.Contains(qid) || stagedExisting.Contains(qid)) continue;

                        var label = ValueOf(b, "itemLabel");
                        var description = ValueOf(b, "itemDescription");
                        var startStr = ValueOf(b, "startDate");
                        var endStr = ValueOf(b, "end");
                        var countryQid = ValueOf(b, "countryQid");
                        var countryLabel = ValueOf(b, "countryLabel");
                        var countryIso = ValueOf(b, "countryIso");
                        var coordStr = ValueOf(b, "coord");

                        if (!DateTime.TryParse(startStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var startDate))
                        {
                            // skip items with invalid start date
                            continue;
                        }

                        DateTime? endDate = null;
                        if (DateTime.TryParse(endStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var tmpEnd))
                            endDate = tmpEnd;

                        double? lat = null;
                        double? lon = null;
                        if (!string.IsNullOrWhiteSpace(coordStr) && coordStr.StartsWith("Point(", StringComparison.OrdinalIgnoreCase))
                        {
                            var inner = coordStr.Substring("Point(".Length).TrimEnd(')');
                            var parts = inner.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 2
                                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLat)
                                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLon))
                            {
                                lat = parsedLat;
                                lon = parsedLon;
                            }
                        }

                        // skip if neither country nor coords
                        if (string.IsNullOrWhiteSpace(countryQid) && lat == null && lon == null) continue;

                        var staged = new StagedRevolution
                        {
                            Name = string.IsNullOrWhiteSpace(label) ? qid : label,
                            StartDate = startDate,
                            EndDate = endDate,
                            Country = countryLabel ?? string.Empty,
                            CountryIso = string.IsNullOrWhiteSpace(countryIso) ? null : countryIso.ToUpperInvariant(),
                            CountryWikidataId = string.IsNullOrWhiteSpace(countryQid) ? null : countryQid,
                            Latitude = lat,
                            Longitude = lon,
                            Description = string.IsNullOrWhiteSpace(description) ? string.Empty : description,
                            WikidataId = qid,
                            Sources = "Wikidata (bulk staging)",
                            Status = "Pending",
                            CreatedAt = DateTime.UtcNow
                        };

                        batch.Add(staged);

                        // mark local sets so we don't add duplicates within this run
                        stagedExisting.Add(qid);
                    }

                    // persist batch in chunks
                    if (batch.Count > 0)
                    {
                        await db.StagedRevolutions.AddRangeAsync(batch, ct);
                        await db.SaveChangesAsync(ct);
                        totalStaged += batch.Count;
                        batch.Clear();
                    }

                    // advance offset and small polite delay
                    offset += limit;
                    await Task.Delay(250, ct);
                }

                return totalStaged;
            }
            finally
            {
                if (createdClient) client.Dispose();
            }
        }

        // New: Fetch staged StagedRevolution objects for a specific set of QIDs (does not persist).
        public static async Task<List<StagedRevolution>> FetchStagedFromQidsAsync(IEnumerable<string> qids, HttpClient? client = null, CancellationToken ct = default)
        {
            if (qids is null) throw new ArgumentNullException(nameof(qids));

            var qList = qids
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Select(q => q!.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (qList.Count == 0) return new List<StagedRevolution>();

            var createdClient = false;
            if (client == null)
            {
                client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
                {
                    Timeout = TimeSpan.FromSeconds(120)
                };
                createdClient = true;
            }

            try
            {
                try { client.DefaultRequestHeaders.UserAgent.ParseAdd("RevolutionsDataMap/0.1 (contact: dev@example.com)"); } catch { }
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

                static string ValueOf(JsonElement el, string name)
                    => el.TryGetProperty(name, out var prop) && prop.TryGetProperty("value", out var v) ? v.GetString() ?? string.Empty : string.Empty;

                var results = new List<StagedRevolution>();
                const int batchSize = 50;

                for (int i = 0; i < qList.Count; i += batchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = qList.Skip(i).Take(batchSize).ToList();
                    var values = string.Join(" ", batch.Select(q => $"wd:{q}"));

                    var query = $@"SELECT DISTINCT ?item ?itemLabel ?itemDescription ?place ?placeLabel ?country ?countryLabel ?countryIso ?countryQid ?startDate ?end ?qid ?coord WHERE {{
  VALUES ?item {{ {values} }}

  OPTIONAL {{ ?item wdt:P276 ?place. ?place wdt:P17 ?countryFromPlace. }}
  OPTIONAL {{ ?item wdt:P131 ?placeAdmin. ?placeAdmin wdt:P17 ?countryFromAdmin. }}

  BIND(COALESCE(?countryFromPlace, ?countryFromAdmin) AS ?country)

  BIND(STRAFTER(STR(?country), 'http://www.wikidata.org/entity/') AS ?countryQid)
  OPTIONAL {{ ?country wdt:P297 ?countryIso. }}
  OPTIONAL {{ ?country rdfs:label ?countryLabel FILTER(LANG(?countryLabel) = 'en') }}

  FILTER(BOUND(?country))

  OPTIONAL {{ ?item wdt:P580 ?s. }}
  OPTIONAL {{ ?item wdt:P585 ?p. }}
  BIND(COALESCE(?s, ?p) AS ?startDate)

  OPTIONAL {{ ?item wdt:P582 ?end. }}
  OPTIONAL {{ ?item schema:description ?itemDescription FILTER(LANG(?itemDescription) = 'en') }}
  OPTIONAL {{ ?item wdt:P625 ?coord. }}
  BIND(STRAFTER(STR(?item), 'http://www.wikidata.org/entity/') AS ?qid)
  SERVICE wikibase:label {{ bd:serviceParam wikibase:language ""[AUTO_LANGUAGE],en"". }}
}}";

                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://query.wikidata.org/sparql")
                    {
                        Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("query", query) })
                    };
                    req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

                    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        // skip this batch on failure but continue
                        continue;
                    }

                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    if (!doc.RootElement.TryGetProperty("results", out var r) || !r.TryGetProperty("bindings", out var bindings))
                        continue;

                    foreach (var b in bindings.EnumerateArray())
                    {
                        var qid = ValueOf(b, "qid");
                        if (string.IsNullOrWhiteSpace(qid)) continue;

                        var label = ValueOf(b, "itemLabel");
                        var description = ValueOf(b, "itemDescription");
                        var startStr = ValueOf(b, "startDate");
                        var endStr = ValueOf(b, "end");
                        var countryQid = ValueOf(b, "countryQid");
                        var countryLabel = ValueOf(b, "countryLabel");
                        var countryIso = ValueOf(b, "countryIso");
                        var coordStr = ValueOf(b, "coord");

                        if (!DateTime.TryParse(startStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var startDate))
                        {
                            // skip if no valid start date
                            continue;
                        }

                        DateTime? endDate = null;
                        if (DateTime.TryParse(endStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var tmpEnd))
                            endDate = tmpEnd;

                        double? lat = null;
                        double? lon = null;
                        if (!string.IsNullOrWhiteSpace(coordStr) && coordStr.StartsWith("Point(", StringComparison.OrdinalIgnoreCase))
                        {
                            var inner = coordStr.Substring("Point(".Length).TrimEnd(')');
                            var parts = inner.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length == 2
                                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLat)
                                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLon))
                            {
                                lat = parsedLat;
                                lon = parsedLon;
                            }
                        }

                        // Skip items that have neither a resolved country nor coordinates
                        if (string.IsNullOrWhiteSpace(countryQid) && lat == null && lon == null)
                            continue;

                        var staged = new StagedRevolution
                        {
                            Name = string.IsNullOrWhiteSpace(label) ? qid : label,
                            StartDate = startDate,
                            EndDate = endDate,
                            Country = countryLabel ?? string.Empty,
                            CountryIso = string.IsNullOrWhiteSpace(countryIso) ? null : countryIso.ToUpperInvariant(),
                            CountryWikidataId = string.IsNullOrWhiteSpace(countryQid) ? null : countryQid,
                            Latitude = lat,
                            Longitude = lon,
                            Description = string.IsNullOrWhiteSpace(description) ? string.Empty : description,
                            WikidataId = qid,
                            Sources = "Wikidata (QID import)",
                            Status = "Pending",
                            CreatedAt = DateTime.UtcNow
                        };

                        results.Add(staged);
                    }

                    // polite delay
                    await Task.Delay(150, ct);
                }

                return results;
            }
            finally
            {
                if (createdClient) client.Dispose();
            }
        }
    }
}