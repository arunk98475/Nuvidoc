namespace Docovee.DS.Entities;

public class DoctorLanguage
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<DoctorDoctorLanguage> Doctors { get; set; } = new List<DoctorDoctorLanguage>();
}
