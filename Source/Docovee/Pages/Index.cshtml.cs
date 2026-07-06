using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages
{
    public class IndexModel : PageModel
    {
        private readonly IProfileService _profileService;
        private readonly IPublicDoctorService _publicDoctorService;

        public IndexModel(IProfileService profileService, IPublicDoctorService publicDoctorService)
        {
            _profileService = profileService;
            _publicDoctorService = publicDoctorService;
        }

        public string? PatientFullName { get; private set; }
        public IReadOnlyList<FeaturedDoctorCardDto> FeaturedDoctors { get; private set; } = Array.Empty<FeaturedDoctorCardDto>();

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            FeaturedDoctors = await _publicDoctorService.GetFeaturedAsync(3, cancellationToken);

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
