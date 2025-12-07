using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Demo.Models;

namespace Demo.Data
{
    public static class WikidataImporter
    {
        private const string Endpoint = "https://query.wikidata.org/sparql";
        private const string SparqlTemplate = @"#replaceLineBreaks
SELECT DISTINCT ?item ?itemLabel ?itemDescription ?place ?placeLabel ?country ?countryLabel ?countryIso ?countryQid ?startDate ?end ?qid ?coord WHERE {
  # candidate country sources only from place/admin → country
  OPTIONAL { ?item wdt:P276 ?place. ?place wdt:P17 ?countryFromPlace. }
  OPTIONAL { ?item wdt:P131 ?placeAdmin. ?placeAdmin wdt:P17 ?countryFromAdmin. }

  # choose place-derived country first, then admin-derived (NO fallback to item P17)
  BIND(COALESCE(?countryFromPlace, ?countryFromAdmin) AS ?country)

  # expose country QID and ISO explicitly
  BIND(STRAFTER(STR(?country), 'http://www.wikidata.org/entity/') AS ?countryQid)
  OPTIONAL { ?country wdt:P297 ?countryIso. }
  OPTIONAL { ?country rdfs:label ?countryLabel FILTER(LANG(?countryLabel) = 'en') }

  # require resolved country be present and that it came via a place/admin
  FILTER(BOUND(?country))

  # 1) Items that are instance-of (P31) or subclass-of (P279*) of revolution (Q10931)
  { ?item wdt:P31/wdt:P279* wd:Q10931. }
  UNION
  # 2) Items whose English description contains common keywords
  { ?item schema:description ?desc .
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

  # coordinates (point) if present
  OPTIONAL { ?item wdt:P625 ?coord. }

  BIND(STRAFTER(STR(?item), 'http://www.wikidata.org/entity/') AS ?qid)

  SERVICE wikibase:label { bd:serviceParam wikibase:language ""[AUTO_LANGUAGE],en"". }
}
ORDER BY ?countryLabel ?startDate ?qid
LIMIT {limit}
OFFSET {offset}";

        // Public entry used by Program.cs - 'limit' is page size when paging through WDQS.
        public static Task<int> FetchAndImportAsync(RevolutionContext db, HttpClient? client = null, CancellationToken ct = default)
            => FetchAndImportByLimitAsync(db, limit: 250, client: client, ct: ct);

        // Fetch pages from WDQS until no more results. 'limit' is page size.
        public static async Task<int> FetchAndImportByLimitAsync(RevolutionContext db, int limit = 250, HttpClient? client = null, CancellationToken ct = default)
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
                // Best-effort user agent; ignore if it fails
                try { client.DefaultRequestHeaders.UserAgent.ParseAdd("RevolutionsDataMap/0.1"); } catch { }

                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

                static string ValueOf(JsonElement el, string name)
                {
                    return el.TryGetProperty(name, out var prop) && prop.TryGetProperty("value", out var v) ? v.GetString() ?? string.Empty : string.Empty;
                }

                var totalImported = 0;
                var offset = 0;
                var seenQids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                while (!ct.IsCancellationRequested)
                {
                    var query = SparqlTemplate
                        .Replace("#replaceLineBreaks", "")
                        .Replace("{limit}", limit.ToString(CultureInfo.InvariantCulture))
                        .Replace("{offset}", offset.ToString(CultureInfo.InvariantCulture));

                    using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                    {
                        Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("query", query) })
                    };
                    req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

                    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync(ct);
                        Console.Error.WriteLine($"Wikidata import failed (status {(int)resp.StatusCode}): {resp.ReasonPhrase}");
                        Console.Error.WriteLine("Response body (truncated): " + (body?.Length > 2000 ? body.Substring(0, 2000) + "..." : body));
                        break;
                    }

                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    if (!doc.RootElement.TryGetProperty("results", out var results) || !results.TryGetProperty("bindings", out var bindings))
                    {
                        Console.Error.WriteLine("Wikidata import: unexpected response structure.");
                        break;
                    }

                    var bindingArray = bindings.EnumerateArray().ToArray();
                    Console.WriteLine($"Wikidata import: fetched {bindingArray.Length} bindings (page size {limit}, offset {offset}).");

                    if (bindingArray.Length == 0) break;

                    // Collect qids for this page, skipping already seen (avoid duplicates)
                    var pageQids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var b in bindingArray)
                    {
                        var q = ValueOf(b, "qid");
                        if (!string.IsNullOrWhiteSpace(q) && !seenQids.Contains(q))
                            pageQids.Add(q);
                    }

                    if (pageQids.Count == 0)
                    {
                        // nothing new on this page — advance to next page
                        offset += limit;
                        await Task.Delay(200, ct);
                        continue;
                    }

                    // Load any existing tracked entities for qids on this page
                    var existingList = await db.Revolutions
                        .Where(r => pageQids.Contains(r.WikidataId))
                        .ToListAsync(ct);

                    var existing = existingList
                        .Where(r => !string.IsNullOrWhiteSpace(r.WikidataId))
                        .ToDictionary(r => r.WikidataId!, StringComparer.OrdinalIgnoreCase);

                    var toAdd = new List<Revolution>();
                    var importedThisPage = 0;

                    foreach (var b in bindingArray)
                    {
                        var qid = ValueOf(b, "qid");
                        if (string.IsNullOrWhiteSpace(qid) || seenQids.Contains(qid))
                            continue;

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
                            Console.Error.WriteLine("Skipping binding with invalid startDate for qid=" + qid + " startStr=" + startStr);
                            seenQids.Add(qid);
                            continue;
                        }

                        DateTime? endDate = null;
                        if (DateTime.TryParse(endStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var tmpEnd))
                            endDate = tmpEnd;

                        double? lat = null;
                        double? lon = null;

                        // coord is typically a WKT literal like "Point(lon lat)"
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

                        // Skip items that have neither a resolved country QID nor coordinates
                        if (string.IsNullOrWhiteSpace(countryQid) && lat == null && lon == null)
                        {
                            Console.Error.WriteLine($"Skipping qid={qid}: no country or coordinates.");
                            seenQids.Add(qid);
                            continue;
                        }

                        if (!existing.TryGetValue(qid, out var entity))
                        {
                            entity = new Revolution { WikidataId = qid };
                            toAdd.Add(entity);
                            existing[qid] = entity; // ensure duplicates within page map to same instance
                        }

                        entity.Name = string.IsNullOrWhiteSpace(label) ? qid : label;
                        entity.StartDate = startDate;
                        entity.EndDate = endDate;
                        entity.Country = countryLabel ?? string.Empty;
                        entity.CountryIso = string.IsNullOrWhiteSpace(countryIso) ? null : countryIso.ToUpperInvariant();
                        entity.CountryWikidataId = string.IsNullOrWhiteSpace(countryQid) ? null : countryQid;
                        entity.Latitude = lat;
                        entity.Longitude = lon;
                        entity.Description = string.IsNullOrWhiteSpace(description) ? string.Empty : description;
                        entity.Type = "Revolution/Uprising";
                        entity.Sources = "Wikidata";

                        importedThisPage++;
                        seenQids.Add(qid);
                    }

                    if (toAdd.Count > 0)
                    {
                        db.Revolutions.AddRange(toAdd);
                    }

                    await db.SaveChangesAsync(ct);

                    totalImported += importedThisPage;

                    // advance offset and small delay to respect WDQS
                    offset += limit;
                    await Task.Delay(200, ct);
                }

                Console.WriteLine($"WikidataImporter: total imported {totalImported} items.");
                return totalImported;
            }
            finally
            {
                if (createdClient) client.Dispose();
            }
        }
    }
}
