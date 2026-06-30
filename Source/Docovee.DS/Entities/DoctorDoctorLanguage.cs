namespace Docovee.DS.Entities;

public class DoctorDoctorLanguage
{
    public int DoctorId { get; set; }
    public Doctor Doctor { get; set; } = null!;
    public int DoctorLanguageId { get; set; }
    public DoctorLanguage DoctorLanguage { get; set; } = null!;
}
