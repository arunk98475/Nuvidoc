namespace Docovee.BLL.Configuration;

public class UploadOptions
{
    public const string SectionName = "Uploads";
    public const long DefaultMaxUploadBytes = 5L * 1024 * 1024;

    public string DoctorsPhysicalPath { get; set; } = string.Empty;
    public string DoctorsPublicPath { get; set; } = "/uploads/doctors";
    public string PatientsPhysicalPath { get; set; } = string.Empty;
    public string PatientsPublicPath { get; set; } = "/uploads/patients";
    public string ContentImagesPhysicalPath { get; set; } = string.Empty;
    public string ContentImagesPublicPath { get; set; } = "/uploads/content";
    public string LegalPdfsPhysicalPath { get; set; } = string.Empty;
    public string LegalPdfsPublicPath { get; set; } = "/uploads/legal";

    /// <summary>
    /// Max upload body size, sourced from web.config maxAllowedContentLength at startup.
    /// </summary>
    public long MaxUploadBytes { get; set; } = DefaultMaxUploadBytes;

    public int MaxUploadMb => Math.Max(1, (int)(MaxUploadBytes / (1024L * 1024L)));
}
