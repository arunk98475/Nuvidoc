namespace Docovee.DS.Entities;

public class DoctorLocation
{
    public int Id { get; set; }
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;

    public string? Name { get; set; }
    public bool InPerson { get; set; } = true;
    public bool VideoVisits { get; set; }
    public string Address1 { get; set; } = string.Empty;
    public string? Address2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string? PhoneExt { get; set; }
    public string? Fax { get; set; }
    public string? ContactPersonName { get; set; }
    public string? AppointmentNotificationEmail { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsPrimary { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
