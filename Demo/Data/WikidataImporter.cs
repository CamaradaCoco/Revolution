using System;
using System.Collections.Generic;
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

        // SPARQL: include english description (schema:description) as ?revolutionDescription
        private const string SparqlTemplate = @"#replaceLineBreaks
SELECT DISTINCT ?revolution ?revolutionLabel ?revolutionDescription ?country ?countryLabel ?countryIso ?startDate ?qid WHERE {
  ?revolution wdt:P31/wdt:P279* wd:Q10931 .
  OPTIONAL { ?revolution wdt:P17 ?country .
             OPTIONAL { ?country rdfs:label ?countryLabel FILTER(LANG(?countryLabel) = 'en') }
             OPTIONAL { ?country wdt:P297 ?countryIso. }
  }
  OPTIONAL { ?revolution wdt:P580 ?startDate. }
  OPTIONAL { ?revolution schema:description ?revolutionDescription FILTER(LANG(?revolutionDescription) = 'en') }
  BIND(STRAFTER(STR(?revolution), 'http://www.wikidata.org/entity/') AS ?qid)
  FILTER(BOUND(?startDate) && YEAR(?startDate) >= 1950)
  SERVICE wikibase:label { bd:serviceParam wikibase:language ""[AUTO_LANGUAGE],en"". }
}
ORDER BY ?countryLabel ?startDate
LIMIT {limit}";

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
                try { client.DefaultRequestHeaders.UserAgent.ParseAdd("RevolutionsDataMap/0.1 (https://github.com/CamaradaCoco/Revolutions; elcosmith@hotmail.com)"); } catch { }
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

                var query = SparqlTemplate.Replace("#replaceLineBreaks", "").Replace("{limit}", limit.ToString());

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

                // small helper to safely get binding string
                static string get(JsonElement binding, string name)
                {
                    if (binding.TryGetProperty(name, out var el) && el.TryGetProperty("value", out var v))
                        return v.GetString() ?? string.Empty;
                    return string.Empty;
                }

                // log a few sample bindings for inspection (truncate long descriptions)
                for (int i = 0; i < Math.Min(6, bindingArray.Length); i++)
                {
                    var b = bindingArray[i];
                    string q = get(b, "qid");
                    string lbl = get(b, "revolutionLabel");
                    string sd = get(b, "startDate");
                    string c = get(b, "countryLabel");
                    string ci = get(b, "countryIso");
                    var desc = get(b, "revolutionDescription");
                    var dshort = desc.Length > 80 ? desc.Substring(0, 77) + "..." : desc;
                    Console.WriteLine($"sample[{i}]: qid={q} label={lbl} start={sd} country={c} iso={ci} desc={dshort}");
                }

                var imported = 0;
                foreach (var b in bindingArray)
                {
                    var qid = get(b, "qid");
                    var label = get(b, "revolutionLabel");
                    var description = get(b, "revolutionDescription");
                    var startStr = get(b, "startDate");
                    var endStr = get(b, "end");
                    var countryLabel = get(b, "countryLabel");
                    var countryIso = get(b, "countryIso");
                    var latStr = get(b, "lat");
                    var lonStr = get(b, "lon");

                    // require qid and start date for deterministic upsert
                    if (string.IsNullOrWhiteSpace(qid))
                    {
                        Console.Error.WriteLine("Skipping binding with no qid (ambiguous): itemLabel=" + label);
                        continue;
                    }
                    if (!DateTime.TryParse(startStr, out var startDate))
                    {
                        Console.Error.WriteLine("Skipping binding with invalid startDate for qid=" + qid + " startStr=" + startStr);
                        continue;
                    }

                    DateTime? endDate = null;
                    if (DateTime.TryParse(endStr, out var tmp)) endDate = tmp;

                    double? lat = null, lon = null;
                    if (double.TryParse(latStr, out var la)) lat = la;
                    if (double.TryParse(lonStr, out var lo)) lon = lo;

                    // Upsert by WikidataId (qid)
                    Revolution? entity = await db.Revolutions.FirstOrDefaultAsync(r => r.WikidataId == qid, ct);
                    if (entity == null)
                    {
                        entity = new Revolution { WikidataId = qid };
                        db.Revolutions.Add(entity);
                    }

                    // store authoritative fields (store description from Wikidata)
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
