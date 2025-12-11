csharp Demo\Data\WikipediaListImporter.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Demo.Data
{
    // Minimal helper to extract linked article titles from a named section,
    // then resolve those titles to Wikidata QIDs via pageprops.wikibase_item.
    public static class WikipediaListImporter
    {
        private const string MediaWikiApi = "https://en.wikipedia.org/w/api.php";

        // Get the section index for a section heading (e.g. "1900s") on a page
        public static async Task<int?> GetSectionIndexAsync(string pageTitle, string sectionHeading, HttpClient? client = null, CancellationToken ct = default)
        {
            client ??= new HttpClient();
            var url = $"{MediaWikiApi}?action=parse&page={Uri.EscapeDataString(pageTitle)}&prop=sections&format=json";
            using var res = await client.GetAsync(url, ct);
            res.EnsureSuccessStatusCode();
            using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("parse", out var parse) || !parse.TryGetProperty("sections", out var sections))
                return null;

            foreach (var s in sections.EnumerateArray())
            {
                var line = s.GetProperty("line").GetString() ?? "";
                if (string.Equals(line.Trim(), sectionHeading.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    if (s.TryGetProperty("index", out var idxEl) && int.TryParse(idxEl.GetString(), out var idx))
                        return idx;
                }
            }
            return null;
        }

        // Get all linked page titles from a given page/section index
        public static async Task<List<string>> GetSectionLinksAsync(string pageTitle, int sectionIndex, HttpClient? client = null, CancellationToken ct = default)
        {
            client ??= new HttpClient();
            var titles = new List<string>();
            string plcontinue = null;

            do
            {
                var url =
                    $"{MediaWikiApi}?action=parse&page={Uri.EscapeDataString(pageTitle)}&prop=links&section={sectionIndex}&format=json";
                // parse->links doesn't support continuation on the parse endpoint; if it becomes necessary
                // you can instead fetch the HTML (prop=text) and parse anchors, or use the page content and extract links.
                using var res = await client.GetAsync(url, ct);
                res.EnsureSuccessStatusCode();
                using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("parse", out var parse) || !parse.TryGetProperty("links", out var links))
                    break;

                foreach (var l in links.EnumerateArray())
                {
                    if (l.TryGetProperty("*", out var titleEl))
                    {
                        var t = titleEl.GetString();
                        if (!string.IsNullOrEmpty(t))
                            titles.Add(t);
                    }
                }

                // parse->links does not provide plcontinue; exit loop
                plcontinue = null;
            } while (!string.IsNullOrEmpty(plcontinue));

            // dedupe
            return titles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Resolve a batch of page titles to Wikidata QIDs using query&prop=pageprops
        public static async Task<Dictionary<string, string?>> ResolveTitlesToQidsAsync(IEnumerable<string> titles, HttpClient? client = null, CancellationToken ct = default)
        {
            client ??= new HttpClient();
            var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            // MediaWiki limits titles per request; use batches (here: 50)
            const int batchSize = 50;
            var list = titles.ToList();
            for (int i = 0; i < list.Count; i += batchSize)
            {
                var batch = list.Skip(i).Take(batchSize).ToList();
                var titlesParam = string.Join("|", batch.Select(Uri.EscapeDataString));
                var url = $"{MediaWikiApi}?action=query&titles={string.Join("|", batch.Select(Uri.EscapeDataString))}&prop=pageprops&format=json&ppprop=wikibase_item";
                using var res = await client.GetAsync(url, ct);
                res.EnsureSuccessStatusCode();
                using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages))
                {
                    // fill nulls for this batch
                    foreach (var t in batch) map[t] = null;
                    continue;
                }

                foreach (var p in pages.EnumerateObject())
                {
                    var pageObj = p.Value;
                    var title = pageObj.GetProperty("title").GetString() ?? "";
                    string? qid = null;
                    if (pageObj.TryGetProperty("pageprops", out var pageprops) && pageprops.TryGetProperty("wikibase_item", out var wbi))
                    {
                        qid = wbi.GetString();
                    }
                    map[title] = qid;
                }

                // ensure any missing titles are present as null
                foreach (var t in batch)
                {
                    if (!map.ContainsKey(t)) map[t] = null;
                }
            }

            return map;
        }
    }
}