using System;

namespace Demo.Models
{
    public class Revolution
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;

        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }

        public string Country { get; set; } = string.Empty;
        public string? CountryIso { get; set; }

        // Added: store the country's Wikidata QID (e.g. "Q142")
        public string? CountryWikidataId { get; set; }

        public double? Latitude { get; set; }
        public double? Longitude { get; set; }

        // Classifications / metadata
        public string Type { get; set; } = string.Empty;
        public int? EstimatedDeaths { get; set; }
        public string Description { get; set; } = string.Empty;

        // Data provenance
        public string? WikidataId { get; set; }    // e.g. "Q12345"
        public string Sources { get; set; } = string.Empty;
        public string Tags { get; set; } = string.Empty;
    }
}
