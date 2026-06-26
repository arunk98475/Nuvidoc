namespace Docovee.BLL.Configuration;

public class UploadOptions
{
    public const string SectionName = "Uploads";
    public string DoctorsPhysicalPath { get; set; } = string.Empty;
    public string DoctorsPublicPath { get; set; } = "/uploads/doctors";
}
