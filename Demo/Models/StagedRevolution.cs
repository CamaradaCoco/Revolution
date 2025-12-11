using System;

namespace Demo.Models
{
    // Staging entity used for manual curation before moving into `Revolution`.
    public class StagedRevolution
    {
        public int Id { get; set; }

        // Basic event metadata
        public string Name { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }

        // Country info
        public string Country { get; set; } = string.Empty;
        public string? CountryIso { get; set; }
        public string? CountryWikidataId { get; set; }

        // Coordinates if available
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }

        // additional metadata
        public string Description { get; set; } = string.Empty;
        public string? WikidataId { get; set; }
        public string Sources { get; set; } = string.Empty;

        // Workflow fields
        public string Status { get; set; } = "Pending"; // Pending / Approved / Rejected
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ReviewedAt { get; set; }
        public string? Reviewer { get; set; }
        public string? ReviewNotes { get; set; }
    }
}