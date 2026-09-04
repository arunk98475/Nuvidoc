using Docovee.DS.Models;

namespace Docovee.Pages.Shared;

public class HomeMarketingModel
{
    public IReadOnlyList<FeaturedDoctorCardDto> FeaturedDoctors { get; set; } = Array.Empty<FeaturedDoctorCardDto>();
    public HomePageContentModel Home { get; set; } = new();
    public int ImplantSpecialistCount { get; set; }
    public decimal AverageGoogleRating { get; set; }
}
