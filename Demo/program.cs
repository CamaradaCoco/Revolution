using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Demo.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

// Add CORS for local development (adjust for production)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// Register DbContext
builder.Services.AddDbContext<RevolutionContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSingleton<Demo.Services.IBackgroundTaskQueue, Demo.Services.BackgroundTaskQueue>();
builder.Services.AddHostedService<Demo.Services.QueuedHostedService>();

var app = builder.Build();

// Run migrations and import (dev) - keep safe default import if desired
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RevolutionContext>();
    try
    {
        db.Database.Migrate();

        try
        {
            var imported = await WikidataImporter.FetchAndImportAsync(db);
            Console.WriteLine($"WikidataImporter: imported {imported} items.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Wikidata import failed: " + ex);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Database migration failed: " + ex);
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseAuthorization();

// Health
app.MapGet("/ping", () => Results.Text("pong"));

// Route listing
app.MapGet("/routes", (EndpointDataSource ds) =>
{
    var list = ds.Endpoints
        .OfType<RouteEndpoint>()
        .Select(e => new { Pattern = e.RoutePattern.RawText, e.DisplayName })
        .OrderBy(e => e.Pattern)
        .ToList();
    return Results.Json(list);
});

// API endpoint for the client map (exact ISO match only)
app.MapGet("/api/revolutions", async (RevolutionContext db, string? country, string? countryIso) =>
{
    if (string.IsNullOrWhiteSpace(country) && string.IsNullOrWhiteSpace(countryIso))
        return Results.BadRequest(new { error = "Provide countryIso or country" });

    // small iso3 -> iso2 map you can extend
    static string? Iso3ToIso2(string iso3)
    {
        if (string.IsNullOrWhiteSpace(iso3)) return null;

        // Source pairs (trimmed example; include full list as needed).
        // Use repeated keys safely by assigning into the dictionary (overwrites duplicates).
        var pairs = new (string Iso3, string Iso2)[]
        {
            ("AFG","AF"),("ALA","AX"),("ALB","AL"),("DZA","DZ"),("ASM","AS"),("AND","AD"),("AGO","AO"),("AIA","AI"),
            ("ATA","AQ"),("ATG","AG"),("ARG","AR"),("ARM","AM"),("ABW","AW"),("AUS","AU"),("AUT","AT"),("AZE","AZ"),
            ("BHS","BS"),("BHR","BH"),("BGD","BD"),("BRB","BB"),("BLR","BY"),("BEL","BE"),("BLZ","BZ"),("BEN","BJ"),
            ("BMU","BM"),("BTN","BT"),("BOL","BO"),("BES","BQ"),("BIH","BA"),("BWA","BW"),("BVT","BV"),("BRA","BR"),
            ("IOT","IO"),("BRN","BN"),("BGR","BG"),("BFA","BF"),("BDI","BI"),("CPV","CV"),("KHM","KH"),("CMR","CM"),
            ("CAN","CA"),("CYM","KY"),("CAF","CF"),("TCD","TD"),("CHL","CL"),("CHN","CN"),("CXR","CX"),("CCK","CC"),
            ("COL","CO"),("COM","KM"),("COG","CG"),("COD","CD"),("COK","CK"),("CRI","CR"),("CIV","CI"),("HRV","HR"),
            ("CUB","CU"),("CUW","CW"),("CYP","CY"),("CZE","CZ"),("DNK","DK"),("DJI","DJ"),("DMA","DM"),("DOM","DO"),
            ("ECU","EC"),("EGY","EG"),("SLV","SV"),("GNQ","GQ"),("ERI","ER"),("EST","EE"),("SWZ","SZ"),("ETH","ET"),
            ("FLK","FK"),("FRO","FO"),("FJI","FJ"),("FIN","FI"),("FRA","FR"),("GUF","GF"),("PYF","PF"),("ATF","TF"),
            ("GAB","GA"),("GMB","GM"),("GEO","GE"),("DEU","DE"),("GHA","GH"),("GIB","GI"),("GRC","GR"),("GRL","GL"),
            ("GRD","GD"),("GLP","GP"),("GUM","GU"),("GTM","GT"),("GGY","GG"),("GIN","GN"),("GNB","GW"),("GUY","GY"),
            ("HTI","HT"),("HMD","HM"),("VAT","VA"),("HND","HN"),("HKG","HK"),("HUN","HU"),("ISL","IS"),("IND","IN"),
            ("IDN","ID"),("IRN","IR"),("IRQ","IQ"),("IRL","IE"),("IMN","IM"),("ISR","IL"),("ITA","IT"),("JAM","JM"),
            ("JPN","JP"),("JEY","JE"),("JOR","JO"),("KAZ","KZ"),("KEN","KE"),("KIR","KI"),("PRK","KP"),("KOR","KR"),
            ("KWT","KW"),("KGZ","KG"),("LAO","LA"),("LVA","LV"),("LBN","LB"),("LSO","LS"),("LBR","LR"),("LBY","LY"),
            ("LIE","LI"),("LTU","LT"),("LUX","LU"),("MAC","MO"),("MDG","MG"),("MWI","MW"),("MYS","MY"),("MDV","MV"),
            ("MLI","ML"),("MLT","MT"),("MHL","MH"),("MTQ","MQ"),("MRT","MR"),("MUS","MU"),("MYT","YT"),("MEX","MX"),
            ("FSM","FM"),("MDA","MD"),("MCO","MC"),("MNG","MN"),("MNE","ME"),("MSR","MS"),("MAR","MA"),("MOZ","MZ"),
            ("MMR","MM"),("NAM","NA"),("NRU","NR"),("NPL","NP"),("NLD","NL"),("NCL","NC"),("NZL","NZ"),("NIC","NI"),
            ("NER","NE"),("NGA","NG"),("NIU","NU"),("NFK","NF"),("MNP","MP"),("NOR","NO"),("OMN","OM"),("PAK","PK"),
            ("PLW","PW"),("PSE","PS"),("PAN","PA"),("PNG","PG"),("PRY","PY"),("PER","PE"),("PHL","PH"),("PCN","PN"),
            ("POL","PL"),("PRT","PT"),("PRI","PR"),("QAT","QA"),("MKD","MK"),("ROU","RO"),("RUS","RU"),("RWA","RW"),
            ("REU","RE"),("BLM","BL"),("SHN","SH"),("KNA","KN"),("LCA","LC"),("MAF","MF"),("SPM","PM"),("VCT","VC"),
            ("WSM","WS"),("SMR","SM"),("STP","ST"),("SAU","SA"),("SEN","SN"),("SRB","RS"),("SYC","SC"),("SLE","SL"),
            ("SGP","SG"),("SXM","SX"),("SVK","SK"),("SVN","SI"),("SLB","SB"),("SOM","SO"),("ZAF","ZA"),("SGS","GS"),
            ("SSD","SS"),("ESP","ES"),("LKA","LK"),("SDN","SD"),("SUR","SR"),("SJM","SJ"),("SWE","SE"),("CHE","CH"),
            ("SYR","SY"),("TWN","TW"),("TJK","TJ"),("TZA","TZ"),("THA","TH"),("TLS","TL"),("TGO","TG"),("TKL","TK"),
            ("TON","TO"),("TTO","TT"),("TUN","TN"),("TUR","TR"),("TKM","TM"),("TCA","TC"),("TUV","TV"),("UGA","UG"),
            ("UKR","UA"),("ARE","AE"),("GBR","GB"),("USA","US"),("UMI","UM"),("URY","UY"),("UZB","UZ"),("VUT","VU"),
            ("VEN","VE"),("VNM","VN"),("VGB","VG"),("VIR","VI"),("WLF","WF"),("ESH","EH"),("YEM","YE"),("ZMB","ZM"),
            ("ZWE","ZW")
        };

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs)
        {
            // assign/overwrite instead of Add to avoid duplicate-key exceptions
            map[k] = v;
        }

        map.TryGetValue(iso3.ToUpperInvariant(), out var result);
        return result;
    }

    // Try exact ISO first (accepts iso2 or iso3)
    if (!string.IsNullOrWhiteSpace(countryIso) || (!string.IsNullOrWhiteSpace(country) && (country.Length == 2 || country.Length == 3)))
    {
        var iso = (countryIso ?? country)!.Trim().ToUpperInvariant();
        var isoCandidates = new List<string> { iso };
        if (iso.Length == 3)
        {
            var iso2 = Iso3ToIso2(iso);
            if (!string.IsNullOrEmpty(iso2) && !isoCandidates.Contains(iso2)) isoCandidates.Add(iso2);
        }
        // also try matching stored alpha-3 values (if any)
        var isoMatches = await db.Revolutions
            .Where(r => r.StartDate.Year >= 1950 && r.CountryIso != null && isoCandidates.Contains(r.CountryIso!.ToUpper()))
            .OrderByDescending(r => r.StartDate)
            .Select(r => new {
                r.Id, r.Name, r.StartDate, r.EndDate, r.Country, r.CountryIso, r.Latitude, r.Longitude, r.Type, r.Description, r.WikidataId
            })
            .ToListAsync();

        if (isoMatches.Any()) return Results.Ok(isoMatches);
        // fall through to name match if no iso results
    }

    // Exact normalized country-name match as fallback (no fuzzy contains)
    if (!string.IsNullOrWhiteSpace(country))
    {
        var searchNorm = NormalizeName(country);
        if (!string.IsNullOrEmpty(searchNorm))
        {
            var rows = await db.Revolutions
                .Where(r => r.StartDate.Year >= 1950 && r.Country != null)
                .Select(r => new {
                    r.Id, r.Name, r.StartDate, r.EndDate, r.Country, r.CountryIso, r.Latitude, r.Longitude, r.Type, r.Description, r.WikidataId
                })
                .ToListAsync();

            var exactMatches = rows
                .Where(r => !string.IsNullOrEmpty(r.Country) && NormalizeName(r.Country) == searchNorm)
                .OrderByDescending(r => r.StartDate)
                .ToList();

            return Results.Ok(exactMatches);
        }
    }

    // nothing matched
    return Results.Ok(Array.Empty<object>());
}).WithName("GetRevolutions");

// Diagnostic endpoints (unchanged)
app.MapGet("/debug/revolutions/sample", async (RevolutionContext db, int take = 20) =>
{
    var rows = await db.Revolutions
        .OrderByDescending(r => r.StartDate)
        .Take(take)
        .Select(r => new { r.Id, r.Name, r.Country, r.CountryIso, r.StartDate, r.Latitude, r.Longitude })
        .ToListAsync();
    return Results.Ok(rows);
});

app.MapGet("/debug/revolutions/counts", async (RevolutionContext db) =>
{
    var counts = await db.Revolutions
        .GroupBy(r => r.CountryIso ?? "(null)")
        .Select(g => new { CountryIso = g.Key, Count = g.Count() })
        .OrderByDescending(x => x.Count)
        .ToListAsync();
    return Results.Ok(counts);
});

app.MapGet("/debug/revolutions/byiso/{iso}", async (RevolutionContext db, string iso) =>
{
    var items = await db.Revolutions
        .Where(r => r.CountryIso != null && r.CountryIso.ToUpper() == iso.ToUpper())
        .Select(r => new { r.Id, r.Name, r.Country, r.CountryIso, r.StartDate })
        .ToListAsync();
    return Results.Ok(items);
});

// Static assets and Razor Pages
app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();

// Add this helper method (place above the Map/Get route registrations)
static string NormalizeName(string? s)
{
    if (string.IsNullOrWhiteSpace(s)) return string.Empty;
    s = s!.Trim();
    // remove diacritics
    var normalizedForm = s.Normalize(NormalizationForm.FormD);
    var sb = new System.Text.StringBuilder();
    foreach (var ch in normalizedForm)
    {
        var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
        if (uc != UnicodeCategory.NonSpacingMark)
            sb.Append(ch);
    }
    var noDiacritics = sb.ToString().Normalize(NormalizationForm.FormC);

    // remove punctuation, replace non-word with space, collapse whitespace
    var cleaned = System.Text.RegularExpressions.Regex.Replace(noDiacritics, @"[^\w\s]", " ");
    cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
    return cleaned.ToLowerInvariant();
}
