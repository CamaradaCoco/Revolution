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

            var url =
                $"{MediaWikiApi}?action=parse&page={Uri.EscapeDataString(pageTitle)}&prop=links&section={sectionIndex}&format=json";
            using var res = await client.GetAsync(url, ct);
            res.EnsureSuccessStatusCode();
            using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("parse", out var parse) || !parse.TryGetProperty("links", out var links))
                return titles;

            foreach (var l in links.EnumerateArray())
            {
                if (l.TryGetProperty("*", out var titleEl))
                {
                    var t = titleEl.GetString();
                    if (!string.IsNullOrEmpty(t))
                        titles.Add(t);
                }
            }

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
                var url = $"{MediaWikiApi}?action=query&titles={titlesParam}&prop=pageprops&format=json&ppprop=wikibase_item";
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

                // polite delay to respect Wikimedia API limits
                await Task.Delay(100, ct);
            }

            return map;
        }

        // New: get all links from the contiguous range of sections between two headings (inclusive).
        // Example: GetSectionLinksInRangeAsync("List_of_revolutions_and_rebellions", "1900s", "2020s", client)
        public static async Task<List<string>> GetSectionLinksInRangeAsync(string pageTitle, string startHeading, string endHeading, HttpClient? client = null, CancellationToken ct = default)
        {
            client ??= new HttpClient();

            // Resolve numeric section indices for start and end headings
            var startIdx = await GetSectionIndexAsync(pageTitle, startHeading, client, ct);
            var endIdx = await GetSectionIndexAsync(pageTitle, endHeading, client, ct);

            if (startIdx == null && endIdx == null)
                return new List<string>();

            // If one of the headings wasn't found, try to use sections list to derive sensible defaults
            if (startIdx == null || endIdx == null)
            {
                // fetch sections list
                var url = $"{MediaWikiApi}?action=parse&page={Uri.EscapeDataString(pageTitle)}&prop=sections&format=json";
                using var res = await client.GetAsync(url, ct);
                res.EnsureSuccessStatusCode();
                using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("parse", out var parse) || !parse.TryGetProperty("sections", out var sections))
                    return new List<string>();

                var secs = sections.EnumerateArray().Select(s => new
                {
                    Index = int.TryParse(s.GetProperty("index").GetString(), out var i) ? i : -1,
                    Line = s.GetProperty("line").GetString() ?? ""
                }).Where(x => x.Index >= 0).ToList();

                if (startIdx == null)
                {
                    // pick first section whose Line contains startHeading text (best-effort)
                    var found = secs.FirstOrDefault(s => string.Equals(s.Line.Trim(), startHeading.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (found != null) startIdx = found.Index;
                    else
                    {
                        // fallback: choose first section whose Line contains "1900" token
                        found = secs.FirstOrDefault(s => s.Line.IndexOf("1900", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (found != null) startIdx = found.Index;
                    }
                }

                if (endIdx == null)
                {
                    var found = secs.FirstOrDefault(s => string.Equals(s.Line.Trim(), endHeading.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (found != null) endIdx = found.Index;
                    else
                    {
                        // fallback: choose last section whose Line contains "2020"
                        found = secs.LastOrDefault(s => s.Line.IndexOf("2020", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (found != null) endIdx = found.Index;
                    }
                }

                if (startIdx == null || endIdx == null)
                    return new List<string>();
            }

            // ensure start <= end
            var sIdx = Math.Min(startIdx.Value, endIdx.Value);
            var eIdx = Math.Max(startIdx.Value, endIdx.Value);

            var allTitles = new List<string>(capacity: (eIdx - sIdx + 1) * 8);
            for (int idx = sIdx; idx <= eIdx; idx++)
            {
                try
                {
                    var titles = await GetSectionLinksAsync(pageTitle, idx, client, ct);
                    if (titles?.Count > 0) allTitles.AddRange(titles);
                    // small polite delay between section requests
                    await Task.Delay(100, ct);
                }
                catch
                {
                    // ignore single-section failures and continue
                }
            }

            return allTitles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}