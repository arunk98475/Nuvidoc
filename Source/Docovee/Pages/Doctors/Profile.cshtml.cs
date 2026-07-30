using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace Docovee.Pages.Doctors;

public class ProfileModel : PageModel
{
    private readonly IPublicDoctorService _publicDoctors;
    private readonly IAppointmentService _appointments;
    private readonly IProfileService _profileService;
    private readonly IInsuranceService _insurance;
    private readonly IPmsCalendarService _pms;
    private readonly ILogger<ProfileModel> _logger;

    public ProfileModel(
        IPublicDoctorService publicDoctors,
        IAppointmentService appointments,
        IProfileService profileService,
        IInsuranceService insurance,
        IPmsCalendarService pms,
        ILogger<ProfileModel> logger)
    {
        _publicDoctors = publicDoctors;
        _appointments = appointments;
        _profileService = profileService;
        _insurance = insurance;
        _pms = pms;
        _logger = logger;
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
        var doctor = await _publicDoctors.GetPublicProfileAsync(id, cancellationToken: cancellationToken);
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
        var rangeStart = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
        var rangeEnd = rangeStart.AddDays(28);
        var booked = await _appointments.GetBookedStartsAsync(doctorId, rangeStart, rangeEnd, cancellationToken);

        try
        {
            if (await _pms.HasEnabledConnectionAsync(doctorId, cancellationToken))
            {
                var pmsSlots = await _pms.GetAvailabilityAsync(doctorId, rangeStart, rangeEnd, 40, cancellationToken);
                if (pmsSlots.Count > 0)
                    return BuildDaysFromPmsSlots(rangeStart, pmsSlots, booked);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PMS availability failed for doctor {DoctorId}; using local slots", doctorId);
        }

        return BuildLocalSyntheticDays(rangeStart, booked);
    }

    private static IReadOnlyList<BookingDayDto> BuildDaysFromPmsSlots(
        DateOnly startDate,
        IReadOnlyList<Docovee.Integrations.Contracts.PmsSlot> pmsSlots,
        HashSet<DateTime> booked)
    {
        var byDate = pmsSlots
            .GroupBy(s => DateOnly.FromDateTime(s.StartsAt))
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.StartsAt).ToList());

        var days = new List<BookingDayDto>();
        var date = startDate;
        while (days.Count < 28)
        {
            IReadOnlyList<BookingSlotDto> slots;
            if (byDate.TryGetValue(date, out var daySlots))
            {
                slots = daySlots.Select(s =>
                {
                    var label = string.IsNullOrWhiteSpace(s.TimeLabel)
                        ? s.StartsAt.ToString("h:mm tt")
                        : s.TimeLabel;
                    return new BookingSlotDto
                    {
                        TimeLabel = label,
                        Available = !AppointmentService.IsSlotBlocked(s.StartsAt, booked)
                    };
                }).ToList();
            }
            else
            {
                slots = Array.Empty<BookingSlotDto>();
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

    private static IReadOnlyList<BookingDayDto> BuildLocalSyntheticDays(DateOnly startDate, HashSet<DateTime> booked)
    {
        var days = new List<BookingDayDto>();
        var date = startDate;
        string[] slotTemplates =
        [
            "9:00 AM", "9:40 AM", "10:20 AM", "11:00 AM",
            "1:30 PM", "2:10 PM", "3:00 PM", "4:20 PM"
        ];

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
                        var available = !AppointmentService.IsSlotBlocked(startsAt, booked);
                        return new BookingSlotDto { TimeLabel = t, Available = available };
                    })
                    .ToList();

                if (built.Count == 0)
                {
                    built.Add(MakeSlot(date, "10:00 AM", booked));
                    built.Add(MakeSlot(date, "2:30 PM", booked));
                }

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
            Available = !AppointmentService.IsSlotBlocked(startsAt, booked)
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
