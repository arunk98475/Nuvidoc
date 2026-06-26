using Docovee.DS.Entities;

namespace Docovee.BLL.Services;

public static class InsuranceMatchHelper
{
    private const int MatchBoost = 12;

    public static IReadOnlyList<string> GetCarrierNames(Doctor doctor)
    {
        return doctor.DoctorInsurances
            .Select(di => di.InsuranceCarrier?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static int InsuranceRankBoost(string? userInsurance, Doctor doctor)
    {
        if (string.IsNullOrWhiteSpace(userInsurance))
            return 0;

        var plan = userInsurance.Trim();
        foreach (var carrier in doctor.DoctorInsurances)
        {
            var name = carrier.InsuranceCarrier?.Name;
            var code = carrier.InsuranceCarrier?.Code;
            if (IsInsuranceMatch(plan, name) || IsInsuranceMatch(plan, code))
                return MatchBoost;
        }

        return 0;
    }

    private static bool IsInsuranceMatch(string userPlan, string? carrierValue)
    {
        if (string.IsNullOrWhiteSpace(carrierValue))
            return false;

        return userPlan.Contains(carrierValue, StringComparison.OrdinalIgnoreCase)
            || carrierValue.Contains(userPlan, StringComparison.OrdinalIgnoreCase);
    }
}
