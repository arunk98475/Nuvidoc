using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages
{
    public class IndexModel : PageModel
    {
        private readonly IProfileService _profileService;
        private readonly IPublicDoctorService _publicDoctorService;
        private readonly IPatientNotificationService _notifications;
        private readonly IHomePageContentService _homePage;
        private readonly IBrandingService _branding;

        public IndexModel(
            IProfileService profileService,
            IPublicDoctorService publicDoctorService,
            IPatientNotificationService notifications,
            IHomePageContentService homePage,
            IBrandingService branding)
        {
            _profileService = profileService;
            _publicDoctorService = publicDoctorService;
            _notifications = notifications;
            _homePage = homePage;
            _branding = branding;
        }

        public string? PatientFullName { get; private set; }
        public int PatientNotifyCount { get; private set; }
        public IReadOnlyList<FeaturedDoctorCardDto> FeaturedDoctors { get; private set; } = Array.Empty<FeaturedDoctorCardDto>();
        public HomePageContentModel Home { get; private set; } = new();
        public int ImplantSpecialistCount { get; private set; }
        public decimal AverageGoogleRating { get; private set; }

        public async Task OnGetAsync(CancellationToken cancellationToken)
        {
            FeaturedDoctors = await _publicDoctorService.GetFeaturedAsync(3, cancellationToken);
            (ImplantSpecialistCount, AverageGoogleRating) =
                await _publicDoctorService.GetHomeTrustStatsAsync(cancellationToken);

            var saved = await _homePage.GetForEditAsync(cancellationToken);
            Home = HomePageContentService.Resolve(saved, _branding.SiteName, _branding.ChatBotName);

            if (User.Identity?.IsAuthenticated != true || !User.IsInRole(AuthRoles.Patient))
                return;

            var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(idClaim, out var patientId))
                return;

            var profile = await _profileService.GetPatientProfileAsync(patientId, cancellationToken);
            PatientFullName = profile?.FullName;
            PatientNotifyCount = await _notifications.CountUnreadAsync(patientId, cancellationToken);
        }
    }
}
