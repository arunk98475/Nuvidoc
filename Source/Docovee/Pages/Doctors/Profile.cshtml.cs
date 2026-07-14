using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Doctors;

public class ProfileModel : PageModel
{
    private readonly IPublicDoctorService _publicDoctors;
    private readonly IAppointmentService _appointments;
    private readonly IProfileService _profileService;
    private readonly IInsuranceService _insurance;

    public ProfileModel(
        IPublicDoctorService publicDoctors,
        IAppointmentService appointments,
        IProfileService profileService,
        IInsuranceService insurance)
    {
        _publicDoctors = publicDoctors;
        _appointments = appointments;
        _profileService = profileService;
        _insurance = insurance;
    }

    public PublicDoctorProfileDto Doctor { get; private set; } = null!;
    public IReadOnlyList<BookingDayDto> BookingDays { get; private set; } = Array.Empty<BookingDayDto>();
    public IReadOnlyList<BookingDayDto> VisibleBookingDays { get; private set; } = Array.Empty<BookingDayDto>();
    public IReadOnlyList<InsuranceCarrierDto> InsuranceCatalog { get; private set; } = Array.Empty<InsuranceCarrierDto>();
    public string PrefillName { get; private set; } = "";
    public string PrefillPhone { get; private set; } = "";
    public string PrefillEmail { get; private set; } = "";
    public string PrefillDateOfBirth { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var doctor = await _publicDoctors.GetPublicProfileAsync(id, cancellationToken);
        if (doctor == null)
            return NotFound();

        Doctor = doctor;
        BookingDays = await BuildBookingDaysAsync(id, cancellationToken);
        VisibleBookingDays = BookingDays.Take(14).ToList();
        InsuranceCatalog = await _insurance.GetCarriersWithPlansAsync(cancellationToken);

        if (User.IsInRole(AuthRoles.Patient)
            && int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
        {
            var patient = await _profileService.GetPatientProfileAsync(patientId);
            if (patient != null)
            {
                PrefillName = patient.FullName;
                PrefillPhone = patient.Phone;
                PrefillEmail = patient.Username;
                if (patient.DateOfBirth != default && patient.DateOfBirth != new DateOnly(1990, 1, 1))
                    PrefillDateOfBirth = patient.DateOfBirth.ToString("yyyy-MM-dd");
            }
        }

        return Page();
    }

    private async Task<IReadOnlyList<BookingDayDto>> BuildBookingDaysAsync(
        int doctorId,
        CancellationToken cancellationToken)
    {
        var days = new List<BookingDayDto>();
        var date = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
        string[] slotTemplates =
        [
            "9:00 AM", "9:40 AM", "10:20 AM", "11:00 AM",
            "1:30 PM", "2:10 PM", "3:00 PM", "4:20 PM"
        ];

        var rangeStart = date;
        var rangeEnd = date.AddDays(28);
        var booked = await _appointments.GetBookedStartsAsync(doctorId, rangeStart, rangeEnd, cancellationToken);

        // Two weeks of consecutive calendar days (weekends shown as no appts), like Zocdoc.
        while (days.Count < 28)
        {
            IReadOnlyList<BookingSlotDto> slots;
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                slots = Array.Empty<BookingSlotDto>();
            }
            else
            {
                var seed = date.DayNumber;
                var built = slotTemplates
                    .Where((_, i) => (seed + i) % 3 != 0)
                    .Take(6)
                    .Select(t =>
                    {
                        AppointmentService.TryParseTimeLabel(t, out var time);
                        var startsAt = date.ToDateTime(time);
                        var available = !booked.Contains(startsAt);
                        return new BookingSlotDto { TimeLabel = t, Available = available };
                    })
                    .ToList();

                if (built.Count == 0)
                {
                    built.Add(MakeSlot(date, "10:00 AM", booked));
                    built.Add(MakeSlot(date, "2:30 PM", booked));
                }

                // Mix of available / empty weekdays for visual realism
                if (seed % 4 == 0)
                    built = built.Select(s => new BookingSlotDto { TimeLabel = s.TimeLabel, Available = false }).ToList();

                slots = built;
            }

            days.Add(new BookingDayDto
            {
                Date = date,
                DateIso = date.ToString("yyyy-MM-dd"),
                DayLabel = date.ToString("ddd"),
                DateLabel = date.ToString("MMM d"),
                AvailableCount = slots.Count(s => s.Available),
                Slots = slots
            });

            date = date.AddDays(1);
        }

        return days;
    }

    private static BookingSlotDto MakeSlot(DateOnly date, string timeLabel, HashSet<DateTime> booked)
    {
        AppointmentService.TryParseTimeLabel(timeLabel, out var time);
        var startsAt = date.ToDateTime(time);
        return new BookingSlotDto
        {
            TimeLabel = timeLabel,
            Available = !booked.Contains(startsAt)
        };
    }
}

public class BookingDayDto
{
    public DateOnly Date { get; set; }
    public string DateIso { get; set; } = string.Empty;
    public string DayLabel { get; set; } = string.Empty;
    public string DateLabel { get; set; } = string.Empty;
    public int AvailableCount { get; set; }
    public IReadOnlyList<BookingSlotDto> Slots { get; set; } = Array.Empty<BookingSlotDto>();
}

public class BookingSlotDto
{
    public string TimeLabel { get; set; } = string.Empty;
    public bool Available { get; set; }
}
