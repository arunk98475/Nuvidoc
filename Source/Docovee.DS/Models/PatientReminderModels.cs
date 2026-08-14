namespace Docovee.DS.Models;

public static class AppointmentReminderKinds
{
    public const string Days7 = "Days7";
    public const string Days3 = "Days3";
    public const string Days1 = "Days1";
    public const string SameDay = "SameDay";
}

public class PatientReminderSettingsDto
{
    public bool Enable7Days { get; set; } = true;
    public string Time7Days { get; set; } = "09:00";
    public bool Enable3Days { get; set; } = true;
    public string Time3Days { get; set; } = "09:00";
    public bool Enable1Day { get; set; } = true;
    public string Time1Day { get; set; } = "09:00";
    public bool EnableSameDay { get; set; } = true;
    public int SameDayHoursBefore { get; set; } = 2;
    public bool ShowNotification { get; set; } = true;
    public bool EnableEmail { get; set; }
    public bool EnableSms { get; set; }

    public bool PhoneVerified { get; set; }
    public bool EmailVerified { get; set; }
    public bool EmailDeliveryAvailable { get; set; }
    public string? PhoneNote { get; set; }
    public string? EmailNote { get; set; }
}

public class PatientReminderSettingsSaveRequest
{
    public bool Enable7Days { get; set; }
    public string? Time7Days { get; set; }
    public bool Enable3Days { get; set; }
    public string? Time3Days { get; set; }
    public bool Enable1Day { get; set; }
    public string? Time1Day { get; set; }
    public bool EnableSameDay { get; set; }
    public int SameDayHoursBefore { get; set; } = 2;
    public bool ShowNotification { get; set; }
    public bool EnableEmail { get; set; }
    public bool EnableSms { get; set; }
}
