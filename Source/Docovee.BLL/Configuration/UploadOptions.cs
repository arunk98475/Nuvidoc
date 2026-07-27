namespace Docovee.BLL.Configuration;

public class UploadOptions
{
    public const string SectionName = "Uploads";
    public string DoctorsPhysicalPath { get; set; } = string.Empty;
    public string DoctorsPublicPath { get; set; } = "/uploads/doctors";
    public string PatientsPhysicalPath { get; set; } = string.Empty;
    public string PatientsPublicPath { get; set; } = "/uploads/patients";
    public string ContentImagesPhysicalPath { get; set; } = string.Empty;
    public string ContentImagesPublicPath { get; set; } = "/uploads/content";
}
