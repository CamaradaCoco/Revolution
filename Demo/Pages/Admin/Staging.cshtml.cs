using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Demo.Data;
using Demo.Models;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using Demo.Services;

namespace Demo.Pages.Admin
{
    public class StagingModel : PageModel
    {
        private readonly RevolutionContext _db;
        private readonly IBackgroundTaskQueue _taskQueue;

        // Full ISO3 -> ISO2 map (static)
        private static readonly Dictionary<string, string> Iso3ToIso2Map = new(StringComparer.OrdinalIgnoreCase)
        {
            {"AFG","AF"},{"ALA","AX"},{"ALB","AL"},{"DZA","DZ"},{"ASM","AS"},{"AND","AD"},{"AGO","AO"},{"AIA","AI"},
            {"ATA","AQ"},{"ATG","AG"},{"ARG","AR"},{"ARM","AM"},{"ABW","AW"},{"AUS","AU"},{"AUT","AT"},{"AZE","AZ"},
            {"BHS","BS"},{"BHR","BH"},{"BGD","BD"},{"BRB","BB"},{"BLR","BY"},{"BEL","BE"},{"BLZ","BZ"},{"BEN","BJ"},
            {"BMU","BM"},{"BTN","BT"},{"BOL","BO"},{"BES","BQ"},{"BIH","BA"},{"BWA","BW"},{"BVT","BV"},{"BRA","BR"},
            {"IOT","IO"},{"BRN","BN"},{"BGR","BG"},{"BFA","BF"},{"BDI","BI"},{"CPV","CV"},{"KHM","KH"},{"CMR","CM"},
            {"CAN","CA"},{"CYM","KY"},{"CAF","CF"},{"TCD","TD"},{"CHL","CL"},{"CHN","CN"},{"CXR","CX"},{"CCK","CC"},
            {"COL","CO"},{"COM","KM"},{"COG","CG"},{"COD","CD"},{"COK","CK"},{"CRI","CR"},{"CIV","CI"},{"HRV","HR"},
            {"CUB","CU"},{"CUW","CW"},{"CYP","CY"},{"CZE","CZ"},{"DNK","DK"},{"DJI","DJ"},{"DMA","DM"},{"DOM","DO"},
            {"ECU","EC"},{"EGY","EG"},{"SLV","SV"},{"GNQ","GQ"},{"ERI","ER"},{"EST","EE"},{"SWZ","SZ"},{"ETH","ET"},
            {"FLK","FK"},{"FRO","FO"},{"FJI","FJ"},{"FIN","FI"},{"FRA","FR"},{"GUF","GF"},{"PYF","PF"},{"ATF","TF"},
            {"GAB","GA"},{"GMB","GM"},{"GEO","GE"},{"DEU","DE"},{"GHA","GH"},{"GIB","GI"},{"GRC","GR"},{"GRL","GL"},
            {"GRD","GD"},{"GLP","GP"},{"GUM","GU"},{"GTM","GT"},{"GGY","GG"},{"GIN","GN"},{"GNB","GW"},{"GUY","GY"},
            {"HTI","HT"},{"HMD","HM"},{"VAT","VA"},{"HND","HN"},{"HKG","HK"},{"HUN","HU"},{"ISL","IS"},{"IND","IN"},
            {"IDN","ID"},{"IRN","IR"},{"IRQ","IQ"},{"IRL","IE"},{"IMN","IM"},{"ISR","IL"},{"ITA","IT"},{"JAM","JM"},
            {"JPN","JP"},{"JEY","JE"},{"JOR","JO"},{"KAZ","KZ"},{"KEN","KE"},{"KIR","KI"},{"PRK","KP"},{"KOR","KR"},
            {"KWT","KW"},{"KGZ","KG"},{"LAO","LA"},{"LVA","LV"},{"LBN","LB"},{"LSO","LS"},{"LBR","LR"},{"LBY","LY"},
            {"LIE","LI"},{"LTU","LT"},{"LUX","LU"},{"MAC","MO"},{"MDG","MG"},{"MWI","MW"},{"MYS","MY"},{"MDV","MV"},
            {"MLI","ML"},{"MLT","MT"},{"MHL","MH"},{"MTQ","MQ"},{"MRT","MR"},{"MUS","MU"},{"MYT","YT"},{"MEX","MX"},
            {"FSM","FM"},{"MDA","MD"},{"MCO","MC"},{"MNG","MN"},{"MNE","ME"},{"MSR","MS"},{"MAR","MA"},{"MOZ","MZ"},
            {"MMR","MM"},{"NAM","NA"},{"NRU","NR"},{"NPL","NP"},{"NLD","NL"},{"NCL","NC"},{"NZL","NZ"},{"NIC","NI"},
            {"NER","NE"},{"NGA","NG"},{"NIU","NU"},{"NFK","NF"},{"MNP","MP"},{"NOR","NO"},{"OMN","OM"},{"PAK","PK"},
            {"PLW","PW"},{"PSE","PS"},{"PAN","PA"},{"PNG","PG"},{"PRY","PY"},{"PER","PE"},{"PHL","PH"},{"PCN","PN"},
            {"POL","PL"},{"PRT","PT"},{"PRI","PR"},{"QAT","QA"},{"MKD","MK"},{"ROU","RO"},{"RUS","RU"},{"RWA","RW"},
            {"REU","RE"},{"BLM","BL"},{"SHN","SH"},{"KNA","KN"},{"LCA","LC"},{"MAF","MF"},{"SPM","PM"},{"VCT","VC"},
            {"WSM","WS"},{"SMR","SM"},{"STP","ST"},{"SAU","SA"},{"SEN","SN"},{"SRB","RS"},{"SYC","SC"},{"SLE","SL"},
            {"SGP","SG"},{"SXM","SX"},{"SVK","SK"},{"SVN","SI"},{"SLB","SB"},{"SOM","SO"},{"ZAF","ZA"},{"SGS","GS"},
            {"SSD","SS"},{"ESP","ES"},{"LKA","LK"},{"SDN","SD"},{"SUR","SR"},{"SJM","SJ"},{"SWE","SE"},{"CHE","CH"},
            {"SYR","SY"},{"TWN","TW"},{"TJK","TJ"},{"TZA","TZ"},{"THA","TH"},{"TLS","TL"},{"TGO","TG"},{"TKL","TK"},
            {"TON","TO"},{"TTO","TT"},{"TUN","TN"},{"TUR","TR"},{"TKM","TM"},{"TCA","TC"},{"TUV","TV"},{"UGA","UG"},
            {"UKR","UA"},{"ARE","AE"},{"GBR","GB"},{"USA","US"},{"UMI","UM"},{"URY","UY"},{"UZB","UZ"},{"VUT","VU"},
            {"VEN","VE"},{"VNM","VN"},{"VGB","VG"},{"VIR","VI"},{"WLF","WF"},{"ESH","EH"},{"YEM","YE"},{"ZMB","ZM"},
            {"ZWE","ZW"}
        };

        public StagingModel(RevolutionContext db, IBackgroundTaskQueue taskQueue)
        {
            _db = db;
            _taskQueue = taskQueue;
        }

        public IQueryable<StagedRevolution> Pending { get; set; } = null!;

        public void OnGet()
        {
            Pending = _db.StagedRevolutions
                .Where(s => s.Status == "Pending")
                .OrderByDescending(s => s.CreatedAt)
                .AsQueryable();
        }

        // Approve staged item: copy to Revolutions and mark staged as Approved
        public async Task<IActionResult> OnPostApproveAsync(int id)
        {
            var staged = await _db.StagedRevolutions.FindAsync(id);
            if (staged == null) return NotFound();

            // copy into live Revolutions table (avoid duplicates by WikidataId)
            if (!string.IsNullOrWhiteSpace(staged.WikidataId))
            {
                var exists = _db.Revolutions.Any(r => r.WikidataId == staged.WikidataId);
                if (exists)
                {
                    staged.Status = "Approved";
                    staged.ReviewedAt = DateTime.UtcNow;
                    staged.Reviewer = User.Identity?.Name;
                    staged.ReviewNotes = "Already existed - marked approved.";
                    await _db.SaveChangesAsync();
                    return RedirectToPage();
                }
            }

            // Normalize ISO to a 2-letter code where possible (helps the client API lookup)
            string? NormalizeIso(string? iso)
            {
                if (string.IsNullOrWhiteSpace(iso)) return null;
                iso = iso!.Trim().ToUpperInvariant();
                if (iso.Length == 2) return iso;
                if (iso.Length == 3)
                {
                    if (Iso3ToIso2Map.TryGetValue(iso, out var iso2)) return iso2;
                }
                return iso; // fallback (may be non-standard)
            }

            var rev = new Revolution
            {
                Name = staged.Name,
                StartDate = staged.StartDate,
                EndDate = staged.EndDate ?? staged.StartDate,
                Country = staged.Country,
                CountryIso = NormalizeIso(staged.CountryIso),
                CountryWikidataId = staged.CountryWikidataId,
                Latitude = staged.Latitude,
                Longitude = staged.Longitude,
                Description = staged.Description,
                WikidataId = staged.WikidataId,
                Sources = staged.Sources,
                Type = "Revolution/Uprising"
            };

            _db.Revolutions.Add(rev);

            staged.Status = "Approved";
            staged.ReviewedAt = DateTime.UtcNow;
            staged.Reviewer = User.Identity?.Name;
            staged.ReviewNotes = "Approved and imported.";

            await _db.SaveChangesAsync();

            // Redirect back to the staging page — the approved item should now be queryable via the API.
            // If the client map still doesn't show it immediately, use the debug endpoints below to confirm:
            // - /debug/revolutions/sample
            // - /debug/revolutions/byiso/{iso}
            // - /debug/revolutions/counts
            return RedirectToPage();
        }

        // Reject staged item (mark as rejected)
        public async Task<IActionResult> OnPostRejectAsync(int id, string? reason)
        {
            var staged = await _db.StagedRevolutions.FindAsync(id);
            if (staged == null) return NotFound();

            staged.Status = "Rejected";
            staged.ReviewedAt = DateTime.UtcNow;
            staged.Reviewer = User.Identity?.Name;
            staged.ReviewNotes = reason ?? "Rejected via admin UI.";

            await _db.SaveChangesAsync();
            return RedirectToPage();
        }

        // Import: use WikipediaListImporter -> resolve QIDs -> fetch staged objects -> insert new staged rows (avoid duplicates)
        public async Task<IActionResult> OnPostImportWikipediaAsync()
        {
            try
            {
                // Create a single HttpClient with a polite User-Agent & Accept headers and pass it to helper methods.
                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                };

                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(120)
                };

                // Wikimedia requires a descriptive User-Agent including contact info (email or URL)
                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("RevolutionsDataMap/0.1 (https://github.com/CamaradaCoco; nsmith196@student.cscc.edu)");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // 1) find 1900s section on the Wikipedia page
                var sec = await WikipediaListImporter.GetSectionIndexAsync("List_of_revolutions_and_rebellions", "1900s", client);
                if (sec == null)
                {
                    TempData["ImportError"] = "Could not find 1900s section on Wikipedia page.";
                    return RedirectToPage();
                }

                // 2) get links (article titles) from the section
                var titles = await WikipediaListImporter.GetSectionLinksAsync("List_of_revolutions_and_rebellions", sec.Value, client);
                if (titles == null || titles.Count == 0)
                {
                    TempData["ImportError"] = "No links found in the 1900s section.";
                    return RedirectToPage();
                }

                // 3) resolve titles -> QIDs
                var map = await WikipediaListImporter.ResolveTitlesToQidsAsync(titles, client);
                var qids = map.Values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (qids.Count == 0)
                {
                    TempData["ImportError"] = "No QIDs resolved from Wikipedia section.";
                    return RedirectToPage();
                }

                // 4) fetch staged candidate objects from Wikidata using the same HttpClient
                var stagedList = await WikidataStagingImporter.FetchStagedFromQidsAsync(qids, client);

                // Insert new staged rows if they don't already exist by WikidataId
                var added = 0;
                foreach (var s in stagedList)
                {
                    if (!string.IsNullOrWhiteSpace(s.WikidataId))
                    {
                        var exists = _db.StagedRevolutions.Any(x => x.WikidataId == s.WikidataId);
                        if (exists) continue;
                    }

                    _db.StagedRevolutions.Add(s);
                    added++;
                }

                await _db.SaveChangesAsync();

                TempData["ImportSuccess"] = $"Imported {stagedList.Count} candidate items into staging ({added} new).";
                return RedirectToPage();
            }
            catch (HttpRequestException hre)
            {
                // network / HTTP error - include message so you can inspect status/details
                TempData["ImportError"] = "Import failed: " + hre.Message;
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                TempData["ImportError"] = "Import failed: " + ex.Message;
                return RedirectToPage();
            }
        }

        // Add this handler method to your existing StagingModel class.
        // It calls the new bulk staging method and reports progress via TempData.
        public async Task<IActionResult> OnPostImportAllWikidataAsync()
        {
            try
            {
                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                };

                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("RevolutionsDataMap/0.1 (https://github.com/CamaradaCoco; nsmith196@student.cscc.edu)");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

                var stagedCount = await WikidataStagingImporter.FetchAndStageAllAsync(_db, client, limit: 250, ct: HttpContext.RequestAborted);

                TempData["ImportSuccess"] = $"Staged {stagedCount} items from Wikidata.";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                TempData["ImportError"] = "Bulk import failed: " + ex.Message;
                return RedirectToPage();
            }
        }

        // add this method inside StagingModel (you already have other handlers)
        public async Task<IActionResult> OnPostStageAllAsync()
        {
            try
            {
                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                };

                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
                // provide a polite User-Agent with contact info per Wikimedia policy
                try { client.DefaultRequestHeaders.UserAgent.ParseAdd("RevolutionsDataMap/0.1 (https://github.com/CamaradaCoco; nsmith196@student.cscc.edu)"); } catch { }
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

                // This method pages WDQS and writes StagedRevolution rows into the DB.
                var stagedCount = await WikidataStagingImporter.FetchAndStageAllAsync(_db, client, limit: 250, ct: HttpContext.RequestAborted);

                TempData["ImportSuccess"] = $"Staged {stagedCount} items from Wikidata.";
                return RedirectToPage();
            }
            catch (OperationCanceledException)
            {
                TempData["ImportError"] = "Staging cancelled (request aborted).";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                TempData["ImportError"] = "Staging failed: " + ex.Message;
                return RedirectToPage();
            }
        }

        // Enqueue background bulk staging job (non-blocking)
        public async Task<IActionResult> OnPostEnqueueImportAllAsync()
        {
            try
            {
                await _taskQueue.QueueBackgroundWorkItem(async ct =>
                {
                    // create an HttpClient for the background job
                    using var handler = new HttpClientHandler
                    {
                        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                    };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
                    try { client.DefaultRequestHeaders.UserAgent.ParseAdd("RevolutionsDataMap/0.1 (https://github.com/CamaradaCoco; nsmith196@student.cscc.edu)"); } catch { }
                    client.DefaultRequestHeaders.Accept.Clear();
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

                    // run the bulk staging importer (it persists staged rows)
                    await WikidataStagingImporter.FetchAndStageAllAsync(_db, client, limit: 100, ct: ct);
                });

                TempData["ImportSuccess"] = "Bulk Wikidata staging job enqueued. It will run in background; check staging page later.";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                TempData["ImportError"] = "Failed to enqueue import: " + ex.Message;
                return RedirectToPage();
            }
        }
    }
}