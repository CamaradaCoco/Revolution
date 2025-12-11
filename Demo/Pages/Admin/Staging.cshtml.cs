using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Demo.Data;
using Demo.Models;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;

namespace Demo.Pages.Admin
{
    public class StagingModel : PageModel
    {
        private readonly RevolutionContext _db;

        public StagingModel(RevolutionContext db) => _db = db;

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

            var rev = new Revolution
            {
                Name = staged.Name,
                StartDate = staged.StartDate,
                EndDate = staged.EndDate ?? staged.StartDate,
                Country = staged.Country,
                CountryIso = staged.CountryIso,
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
    }
}