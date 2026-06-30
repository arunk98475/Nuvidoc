using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.DS;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Docovee.Pages.Account.Register;

public class DoctorModel : PageModel
{
    private readonly DocoveeDbContext _db;

    public DoctorModel(DocoveeDbContext db) => _db = db;

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            if (User.IsInRole(AuthRoles.Doctor))
            {
                var doctorIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (int.TryParse(doctorIdClaim, out var doctorId))
                {
                    var doctor = await _db.Doctors.AsNoTracking()
                        .FirstOrDefaultAsync(d => d.Id == doctorId);
                    if (doctor != null && DoctorOnboardingProgress.IsOnboardingComplete(doctor))
                        return Redirect("/Account/DoctorProfile");
                }
                return Page();
            }
            return Redirect("/");
        }
        return Page();
    }
}
