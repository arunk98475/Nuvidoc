using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.BLL.Services.Billing;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Doctor;

[Authorize(Roles = AuthRoles.Doctor)]
public class PerformanceModel : PageModel
{
    private readonly IDoctorSponsorshipService _sponsorship;
    private readonly IDoctorBillingService _billing;

    public PerformanceModel(
        IDoctorSponsorshipService sponsorship,
        IDoctorBillingService billing)
    {
        _sponsorship = sponsorship;
        _billing = billing;
    }

    public DoctorSponsorshipStatusDto Sponsorship { get; private set; } = new();
    public DoctorPerformanceOverviewDto Overview { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return;

        Sponsorship = await _sponsorship.GetStatusAsync(doctorId, cancellationToken) ?? new DoctorSponsorshipStatusDto();
        Overview = await _billing.GetPerformanceOverviewAsync(doctorId, cancellationToken);
    }
}
