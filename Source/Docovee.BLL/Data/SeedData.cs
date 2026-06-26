using Docovee.BLL.Configuration;
using Docovee.BLL.Services;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Enums;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Data;

public static class SeedData
{
    public static async Task InitializeAsync(DocoveeDbContext context)
    {
        if (await context.Doctors.AnyAsync())
            return;

        var carriers = new[]
        {
            new InsuranceCarrier { Name = "Aetna PPO", Code = "AETNA" },
            new InsuranceCarrier { Name = "Cigna", Code = "CIGNA" },
            new InsuranceCarrier { Name = "United Healthcare", Code = "UNITED" },
            new InsuranceCarrier { Name = "Blue Cross Blue Shield", Code = "BCBS" },
            new InsuranceCarrier { Name = "Medicare", Code = "MEDICARE" },
            new InsuranceCarrier { Name = "Humana", Code = "HUMANA" }
        };
        context.InsuranceCarriers.AddRange(carriers);
        await context.SaveChangesAsync();

        var doctors = new List<Doctor>
        {
            new()
            {
                Name = "Dr. Sarah Kim, DDS",
                Specialty = "General Dentist · Cosmetic Focus",
                SpecialtyCategory = "General Dentist",
                City = "Renton", State = "WA", ZipCode = "98055",
                Latitude = 47.4829, Longitude = -122.2171,
                GoogleRating = 4.9m, GoogleReviewCount = 214,
                AvatarInitials = "SK", TagLine = "Best Match",
                Gender = Gender.Female,
                OfficePhoneNumber = "(425) 555-0101",
                YearsOfPractice = 14
            },
            new()
            {
                Name = "Dr. Marcus Chen, DDS",
                Specialty = "General Dentist · Family Practice",
                SpecialtyCategory = "General Dentist",
                City = "Renton", State = "WA", ZipCode = "98057",
                Latitude = 47.4789, Longitude = -122.2050,
                GoogleRating = 4.7m, GoogleReviewCount = 189,
                AvatarInitials = "MC", TagLine = "Family Friendly",
                Gender = Gender.Male,
                OfficePhoneNumber = "(425) 555-0102",
                YearsOfPractice = 10
            },
            new()
            {
                Name = "Dr. Aisha Patel, DDS",
                Specialty = "General Dentist · Preventive Care",
                SpecialtyCategory = "General Dentist",
                City = "Tukwila", State = "WA", ZipCode = "98188",
                Latitude = 47.4740, Longitude = -122.2856,
                GoogleRating = 4.8m, GoogleReviewCount = 156,
                AvatarInitials = "AP", TagLine = "Same-Day Available",
                Gender = Gender.Female,
                OfficePhoneNumber = "(206) 555-0103",
                YearsOfPractice = 8
            },
            new()
            {
                Name = "Dr. James Wilson, MD",
                Specialty = "Family Medicine",
                SpecialtyCategory = "Family Medicine",
                City = "Renton", State = "WA", ZipCode = "98055",
                Latitude = 47.4850, Longitude = -122.2100,
                GoogleRating = 4.8m, GoogleReviewCount = 302,
                AvatarInitials = "JW", TagLine = "Highly Rated",
                Gender = Gender.Male,
                OfficePhoneNumber = "(425) 555-0201",
                YearsOfPractice = 18
            },
            new()
            {
                Name = "Dr. Emily Rodriguez, MD",
                Specialty = "Internal Medicine",
                SpecialtyCategory = "Internal Medicine",
                City = "Renton", State = "WA", ZipCode = "98056",
                Latitude = 47.4900, Longitude = -122.1950,
                GoogleRating = 4.9m, GoogleReviewCount = 278,
                AvatarInitials = "ER", TagLine = "Thorough Care",
                Gender = Gender.Female,
                OfficePhoneNumber = "(425) 555-0202",
                YearsOfPractice = 12
            },
            new()
            {
                Name = "Dr. Robert Taylor, MD",
                Specialty = "Orthopedic Surgeon",
                SpecialtyCategory = "Orthopedic Surgeon",
                City = "Seattle", State = "WA", ZipCode = "98101",
                Latitude = 47.6062, Longitude = -122.3321,
                GoogleRating = 4.7m, GoogleReviewCount = 198,
                AvatarInitials = "RT", TagLine = "Back & Joint Specialist",
                Gender = Gender.Male,
                OfficePhoneNumber = "(206) 555-0301",
                YearsOfPractice = 20
            },
            new()
            {
                Name = "Dr. Lisa Nguyen, MD",
                Specialty = "Dermatologist",
                SpecialtyCategory = "Dermatologist",
                City = "Bellevue", State = "WA", ZipCode = "98004",
                Latitude = 47.6101, Longitude = -122.2015,
                GoogleRating = 4.8m, GoogleReviewCount = 167,
                AvatarInitials = "LN", TagLine = "Skin Care Expert",
                Gender = Gender.Female,
                OfficePhoneNumber = "(425) 555-0401",
                YearsOfPractice = 11
            },
            new()
            {
                Name = "Dr. Michael Brooks, MD",
                Specialty = "Cardiologist",
                SpecialtyCategory = "Cardiologist",
                City = "Seattle", State = "WA", ZipCode = "98104",
                Latitude = 47.6038, Longitude = -122.3301,
                GoogleRating = 4.9m, GoogleReviewCount = 245,
                AvatarInitials = "MB", TagLine = "Heart Health",
                Gender = Gender.Male,
                OfficePhoneNumber = "(206) 555-0501",
                YearsOfPractice = 22
            }
        };

        context.Doctors.AddRange(doctors);
        await context.SaveChangesAsync();

        var carrierMap = await context.InsuranceCarriers.ToDictionaryAsync(c => c.Code, c => c.Id);

        foreach (var doctor in doctors)
        {
            foreach (var code in carrierMap.Keys)
            {
                context.DoctorInsurances.Add(new DoctorInsurance
                {
                    DoctorId = doctor.Id,
                    InsuranceCarrierId = carrierMap[code]
                });
            }
        }

        await context.SaveChangesAsync();
    }

    public static async Task InitializeAdminAndSettingsAsync(DocoveeDbContext context, AdminOptions adminOptions)
    {
        await AdminSeedHelper.EnsureAdminAsync(context, adminOptions);

        if (!await context.AppSettings.AnyAsync(s => s.Key == AppSettingKeys.DoctorSearchResultCount))
        {
            context.AppSettings.Add(new AppSetting
            {
                Key = AppSettingKeys.DoctorSearchResultCount,
                Value = "10"
            });
        }

        if (!await context.AppSettings.AnyAsync(s => s.Key == AppSettingKeys.PromotedDoctorIds))
        {
            context.AppSettings.Add(new AppSetting
            {
                Key = AppSettingKeys.PromotedDoctorIds,
                Value = string.Empty
            });
        }

        if (!await context.AppSettings.AnyAsync(s => s.Key == AppSettingKeys.MaxAiQuestions))
        {
            context.AppSettings.Add(new AppSetting
            {
                Key = AppSettingKeys.MaxAiQuestions,
                Value = "3"
            });
        }

        if (!await context.PollingQuestions.AnyAsync())
        {
            context.PollingQuestions.AddRange(
                new PollingQuestion { Question = "Does experience level matter to you, or are you more focused on bedside manner and vibe?", ValidationHint = "Expect a preference about experience vs bedside manner", SortOrder = 1 },
                new PollingQuestion { Question = "Do you have a gender preference for your doctor?", ValidationHint = "Expect male, female, or no preference", SortOrder = 2 },
                new PollingQuestion { Question = "How do you prefer your doctor to communicate — direct and efficient, collaborative and thorough, or warm and reassuring?", ValidationHint = "Expect a communication style preference", SortOrder = 3 },
                new PollingQuestion { Question = "How far are you willing to travel for the right doctor?", ValidationHint = "Expect a distance or location preference", SortOrder = 4 },
                new PollingQuestion { Question = "Is it important that your doctor offers telehealth visits?", ValidationHint = "Expect yes, no, or sometimes", SortOrder = 5 },
                new PollingQuestion { Question = "Do you prefer a doctor who takes time to explain things, or one who moves quickly?", ValidationHint = "Expect a pace preference", SortOrder = 6 },
                new PollingQuestion { Question = "Are you looking for a long-term doctor relationship or a one-time visit?", ValidationHint = "Expect long-term or one-time", SortOrder = 7 },
                new PollingQuestion { Question = "Anything else that would make a doctor feel like the right fit for you?", ValidationHint = "Expect any free-text preference", SortOrder = 8 }
            );
        }

        await context.SaveChangesAsync();
    }
}
