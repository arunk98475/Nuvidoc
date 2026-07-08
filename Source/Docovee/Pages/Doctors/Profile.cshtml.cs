using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Doctors;

public class ProfileModel : PageModel
{
    private readonly IPublicDoctorService _publicDoctors;

    public ProfileModel(IPublicDoctorService publicDoctors) => _publicDoctors = publicDoctors;

    public PublicDoctorProfileDto Doctor { get; private set; } = null!;
    public IReadOnlyList<BookingDayDto> BookingDays { get; private set; } = Array.Empty<BookingDayDto>();

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var doctor = await _publicDoctors.GetPublicProfileAsync(id, cancellationToken);
        if (doctor == null)
            return NotFound();

        Doctor = doctor;
        BookingDays = BuildMockBookingDays();
        return Page();
    }

    private static IReadOnlyList<BookingDayDto> BuildMockBookingDays()
    {
        var days = new List<BookingDayDto>();
        var date = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
        string[] slotTemplates = ["9:00 AM", "9:40 AM", "10:20 AM", "11:00 AM", "1:30 PM", "2:10 PM", "3:00 PM", "4:20 PM"];

        while (days.Count < 5)
        {
            if (date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                var seed = date.DayNumber;
                var slots = slotTemplates
                    .Where((_, i) => (seed + i) % 3 != 0)
                    .Take(5)
                    .Select(t => new BookingSlotDto { TimeLabel = t, Available = true })
                    .ToList();

                if (slots.Count == 0)
                {
                    slots.Add(new BookingSlotDto { TimeLabel = "10:00 AM", Available = true });
                    slots.Add(new BookingSlotDto { TimeLabel = "2:30 PM", Available = true });
                }

                days.Add(new BookingDayDto
                {
                    Date = date,
                    DayLabel = date.ToString("ddd"),
                    DateLabel = date.ToString("MMM d"),
                    Slots = slots
                });
            }

            date = date.AddDays(1);
        }

        return days;
    }
}

public class BookingDayDto
{
    public DateOnly Date { get; set; }
    public string DayLabel { get; set; } = string.Empty;
    public string DateLabel { get; set; } = string.Empty;
    public IReadOnlyList<BookingSlotDto> Slots { get; set; } = Array.Empty<BookingSlotDto>();
}

public class BookingSlotDto
{
    public string TimeLabel { get; set; } = string.Empty;
    public bool Available { get; set; }
}
