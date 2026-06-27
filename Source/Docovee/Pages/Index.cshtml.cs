using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages
{
    public class IndexModel : PageModel
    {
        private readonly IProfileService _profileService;

        public IndexModel(IProfileService profileService)
        {
            _profileService = profileService;
        }

        public string? PatientFullName { get; private set; }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            if (User.Identity?.IsAuthenticated != true || !User.IsInRole(AuthRoles.Patient))
                return;

            var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(idClaim, out var patientId))
                return;

            var profile = await _profileService.GetPatientProfileAsync(patientId, cancellationToken);
            PatientFullName = profile?.FullName;
        }
    }
}
