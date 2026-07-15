namespace Docovee.DS.Models;

public class DoctorLocationDto
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string FormattedAddress { get; set; } = string.Empty;
    public string LocationTypeLabel { get; set; } = string.Empty;
    public string PhoneDisplay { get; set; } = string.Empty;
    public string EmailDisplay { get; set; } = string.Empty;
}

public class DoctorLocationInput
{
    public int? Id { get; set; }
    public string? Name { get; set; }
    public bool InPerson { get; set; } = true;
    public bool VideoVisits { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public string? PhoneNumber { get; set; }
    public string? PhoneExt { get; set; }
    public string? Fax { get; set; }
    public string? ContactPersonName { get; set; }
    public string? AppointmentNotificationEmail { get; set; }
}

public class SaveDoctorLocationsInput
{
    public List<DoctorLocationInput> Locations { get; set; } = new();
}
