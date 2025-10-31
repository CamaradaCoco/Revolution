using System;
using System.Collections.Generic;
using System.Globalization;
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
SELECT DISTINCT ?item ?itemLabel ?itemDescription ?country ?countryLabel ?countryIso ?startDate ?end ?qid WHERE {
  { ?item wdt:P31/wdt:P279* wd:Q10931. }
  UNION
  { ?item rdfs:label ?lab .
    FILTER(LANG(?lab) = 'en' &&
      (CONTAINS(LCASE(?lab),'revolution') ||
       CONTAINS(LCASE(?lab),'uprising') ||
       CONTAINS(LCASE(?lab),'rebellion') ||
       CONTAINS(LCASE(?lab),'insurgency') ||
       CONTAINS(LCASE(?lab),'coup') ||
       CONTAINS(LCASE(?lab),'protest')))
  }
  UNION
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

  OPTIONAL {
    ?item wdt:P17 ?country.
    OPTIONAL { ?country rdfs:label ?countryLabel FILTER(LANG(?countryLabel) = 'en') }
    OPTIONAL { ?country wdt:P297 ?countryIso. }
  }

  OPTIONAL { ?item schema:description ?itemDescription FILTER(LANG(?itemDescription) = 'en') }

  BIND(STRAFTER(STR(?item), 'http://www.wikidata.org/entity/') AS ?qid)

  SERVICE wikibase:label { bd:serviceParam wikibase:language ""[AUTO_LANGUAGE],en"". }
}
ORDER BY ?countryLabel ?startDate
LIMIT {500}";

        // Public entry used by Program.cs
        public static Task<int> FetchAndImportAsync(RevolutionContext db, HttpClient? client = null, CancellationToken ct = default)
            => FetchAndImportByLimitAsync(db, limit: 250, client: client, ct: ct);

        // Single-page import (use small limit while testing in WDQS)
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

                var query = SparqlTemplate.Replace("#replaceLineBreaks", "").Replace("{limit}", limit.ToString(CultureInfo.InvariantCulture));

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
                    return 0;
                }

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("results", out var results) || !results.TryGetProperty("bindings", out var bindings))
                {
                    Console.Error.WriteLine("Wikidata import: unexpected response structure.");
                    return 0;
                }

                var bindingArray = bindings.EnumerateArray().ToArray();
                Console.WriteLine($"Wikidata import: fetched {bindingArray.Length} bindings (limit {limit}).");

                static string ValueOf(JsonElement el, string name)
                {
                    return el.TryGetProperty(name, out var prop) && prop.TryGetProperty("value", out var v) ? v.GetString() ?? string.Empty : string.Empty;
                }

                // Collect qids for a single DB query (avoid N queries)
                var qids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in bindingArray)
                {
                    var q = ValueOf(b, "qid");
                    if (!string.IsNullOrWhiteSpace(q)) qids.Add(q);
                }

                var existing = new Dictionary<string, Revolution>(StringComparer.OrdinalIgnoreCase);
                if (qids.Count > 0)
                {
                    var list = await db.Revolutions
                        .AsNoTracking()
                        .Where(r => qids.Contains(r.WikidataId))
                        .ToListAsync(ct);

                    foreach (var r in list)
                        if (!string.IsNullOrWhiteSpace(r.WikidataId))
                            existing[r.WikidataId!] = r;
                }

                // We'll track newly created entities here so we can update them without another DB lookup
                var toAdd = new List<Revolution>();
                var imported = 0;

                for (int i = 0; i < bindingArray.Length; i++)
                {
                    var b = bindingArray[i];

                    var qid = ValueOf(b, "qid");
                    var label = ValueOf(b, "itemLabel");
                    var description = ValueOf(b, "itemDescription");
                    var startStr = ValueOf(b, "startDate");
                    var endStr = ValueOf(b, "end");
                    var countryLabel = ValueOf(b, "countryLabel");
                    var countryIso = ValueOf(b, "countryIso");
                    var latStr = ValueOf(b, "lat");
                    var lonStr = ValueOf(b, "lon");

                    if (string.IsNullOrWhiteSpace(qid))
                    {
                        Console.Error.WriteLine("Skipping binding with no qid (ambiguous): itemLabel=" + label);
                        continue;
                    }

                    if (!DateTime.TryParse(startStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var startDate))
                    {
                        Console.Error.WriteLine("Skipping binding with invalid startDate for qid=" + qid + " startStr=" + startStr);
                        continue;
                    }

                    DateTime? endDate = null;
                    if (DateTime.TryParse(endStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var tmpEnd))
                        endDate = tmpEnd;

                    double? lat = double.TryParse(latStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var la) ? la : null;
                    double? lon = double.TryParse(lonStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) ? lo : null;

                    if (!existing.TryGetValue(qid, out var entity))
                    {
                        entity = new Revolution { WikidataId = qid };
                        toAdd.Add(entity);
                        existing[qid] = entity; // keep in dictionary so duplicates in the results map to same instance
                    }

                    entity.Name = string.IsNullOrWhiteSpace(label) ? qid : label;
                    entity.StartDate = startDate;
                    entity.EndDate = endDate;
                    entity.Country = countryLabel ?? string.Empty;
                    entity.CountryIso = string.IsNullOrWhiteSpace(countryIso) ? null : countryIso.ToUpperInvariant();
                    entity.Latitude = lat;
                    entity.Longitude = lon;
                    entity.Description = string.IsNullOrWhiteSpace(description) ? string.Empty : description;
                    entity.Type = "Revolution/Uprising";
                    entity.Sources = "Wikidata";

                    imported++;
                }

                // Attach new entities and save once
                if (toAdd.Count > 0)
                {
                    db.Revolutions.AddRange(toAdd);
                }

                await db.SaveChangesAsync(ct);
                Console.WriteLine($"WikidataImporter: imported {imported} items.");
                return imported;
            }
            finally
            {
                if (createdClient) client.Dispose();
            }
        }
    }
}
