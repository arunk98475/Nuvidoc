using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Doctor;

[Authorize(Roles = AuthRoles.Doctor)]
public class PatientModel : PageModel
{
    private readonly IAppointmentService _appointments;

    public PatientModel(IAppointmentService appointments) => _appointments = appointments;

    public async Task<IActionResult> OnGetAsync(int appointmentId, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var appointment = await _appointments.GetForDoctorByIdAsync(doctorId, appointmentId, cancellationToken);
        if (appointment == null)
            return NotFound();

        var week = DateOnly.FromDateTime(appointment.StartsAt).ToString("yyyy-MM-dd");
        return Redirect($"/Doctor/Calendar?week={week}&appointmentId={appointmentId}");
    }
}
