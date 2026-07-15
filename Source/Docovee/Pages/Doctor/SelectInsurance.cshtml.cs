using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Doctor.SettingsPages;

[Authorize(Roles = AuthRoles.Doctor)]
public class SelectInsuranceModel : PageModel
{
    private static readonly string[] PopularNames =
    [
        "Aetna",
        "Cigna",
        "Delta Dental",
        "UnitedHealthcare",
        "MetLife",
        "Guardian",
        "Humana",
        "Blue Cross Blue Shield",
        "BCBS",
        "Medicare",
        "Medicaid",
        "Premera Blue Cross",
        "United Concordia",
        "Principal",
        "GEHA",
        "Anthem",
        "Tricare",
        "Self-Pay / Cash"
    ];

    private readonly IDoctorInsuranceService _insuranceService;

    public SelectInsuranceModel(IDoctorInsuranceService insuranceService) => _insuranceService = insuranceService;

    [BindProperty]
    public SelectInsuranceInput Input { get; set; } = new();

    public IReadOnlyList<SelectableInsuranceCarrierDto> PopularCarriers { get; private set; } = Array.Empty<SelectableInsuranceCarrierDto>();
    public IReadOnlyList<SelectableInsuranceCarrierDto> OtherCarriers { get; private set; } = Array.Empty<SelectableInsuranceCarrierDto>();
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        await LoadAsync(doctorId, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var (success, error) = await _insuranceService.SetCarriersAsync(doctorId, Input.CarrierIds ?? new(), cancellationToken);
        if (!success)
        {
            ErrorMessage = error;
            await LoadAsync(doctorId, cancellationToken);
            return Page();
        }

        return RedirectToPage("/Doctor/Settings", new { section = "insurance", saved = true });
    }

    private async Task LoadAsync(int doctorId, CancellationToken cancellationToken)
    {
        var carriers = await _insuranceService.GetSelectableCarriersAsync(doctorId, cancellationToken);

        if (Input.CarrierIds.Count > 0)
        {
            var selected = Input.CarrierIds.ToHashSet();
            carriers = carriers.Select(c => new SelectableInsuranceCarrierDto
            {
                Id = c.Id,
                Name = c.Name,
                Code = c.Code,
                Selected = selected.Contains(c.Id)
            }).ToList();
        }

        var popularSet = PopularNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var popularOrder = PopularNames
            .Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);

        PopularCarriers = carriers
            .Where(c => popularSet.Contains(c.Name))
            .OrderBy(c => popularOrder.TryGetValue(c.Name, out var i) ? i : 999)
            .ThenBy(c => c.Name)
            .ToList();

        OtherCarriers = carriers
            .Where(c => !popularSet.Contains(c.Name))
            .OrderBy(c => c.Name)
            .ToList();
    }
}
