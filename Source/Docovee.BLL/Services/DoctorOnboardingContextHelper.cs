using System.Text.Json;
using Docovee.DS.Models;
using Docovee.DS.Entities;

namespace Docovee.BLL.Services;

public static class DoctorOnboardingContextHelper
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static DoctorOnboardingContextData Load(DoctorOnboardingSession session)
    {
        if (string.IsNullOrWhiteSpace(session.ContextJson))
            return new DoctorOnboardingContextData();

        try
        {
            return JsonSerializer.Deserialize<DoctorOnboardingContextData>(session.ContextJson, Options)
                   ?? new DoctorOnboardingContextData();
        }
        catch
        {
            return new DoctorOnboardingContextData();
        }
    }

    public static void Save(DoctorOnboardingSession session, DoctorOnboardingContextData context) =>
        session.ContextJson = JsonSerializer.Serialize(context, Options);
}
