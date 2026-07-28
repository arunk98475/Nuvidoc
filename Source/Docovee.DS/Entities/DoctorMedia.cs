namespace Docovee.DS.Entities;

public static class DoctorMediaTypes
{
    public const string Clinic = "Clinic";
    public const string Team = "Team";
    public const string Smile = "Smile";
    public const string Family = "Family";
    public const string Pets = "Pets";

    public static readonly string[] All = [Clinic, Team, Smile, Family, Pets];

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && All.Any(t => string.Equals(t, value.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string value) =>
        All.First(t => string.Equals(t, value.Trim(), StringComparison.OrdinalIgnoreCase));
}

public class DoctorMedia
{
    public int Id { get; set; }
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public int? LocationId { get; set; }
    public DoctorLocation? Location { get; set; }
    public string MediaType { get; set; } = DoctorMediaTypes.Clinic;
    public string Url { get; set; } = string.Empty;
    public string? Caption { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
