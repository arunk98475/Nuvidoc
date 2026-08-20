using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Doctor;

[Authorize(Roles = AuthRoles.Doctor)]
public class PerformanceModel : PageModel
{
    private readonly IDoctorSponsorshipService _sponsorship;

    public PerformanceModel(IDoctorSponsorshipService sponsorship)
    {
        _sponsorship = sponsorship;
    }

    public DoctorSponsorshipStatusDto Sponsorship { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return;

        Sponsorship = await _sponsorship.GetStatusAsync(doctorId, cancellationToken) ?? new DoctorSponsorshipStatusDto();
    }
}
