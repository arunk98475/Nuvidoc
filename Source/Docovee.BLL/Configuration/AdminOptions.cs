namespace Docovee.BLL.Configuration;

public class AdminOptions
{
    public const string SectionName = "Admin";
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "Admin@123";
}

public static class AppSettingKeys
{
    public const string DoctorSearchResultCount = "DoctorSearchResultCount";
    public const string PromotedDoctorIds = "PromotedDoctorIds";
    public const string MaxAiQuestions = "MaxAiQuestions";
}
