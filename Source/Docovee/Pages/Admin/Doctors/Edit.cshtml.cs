using Docovee.BLL.Configuration;
using Docovee.BLL.Services;
using Docovee.DS.Models;
using Docovee.Integrations.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Docovee.Pages.Admin.Doctors;

public class EditModel : PageModel
{
    private readonly IAdminDoctorService _doctorService;
    private readonly IDoctorPracticeFeeService _practiceFeeService;
    private readonly IPmsCalendarService _pms;
    private readonly AccountOptions _account;

    public EditModel(
        IAdminDoctorService doctorService,
        IDoctorPracticeFeeService practiceFeeService,
        IPmsCalendarService pms,
        IOptions<AccountOptions> account)
    {
        _doctorService = doctorService;
        _practiceFeeService = practiceFeeService;
        _pms = pms;
        _account = account.Value;
    }

    [BindProperty]
    public DoctorAdminEditModel Input { get; set; } = new();

    [BindProperty]
    public NexHealthIntegrationInput NexHealthForm { get; set; } = new();

    [BindProperty]
    public DoctorPracticeFeeInput PracticeFeeForm { get; set; } = new();

    public PmsConnectionSettingsDto? NexHealthConnection { get; set; }
    public IReadOnlyList<DoctorPracticeFeeDto> PracticeFees { get; private set; } = Array.Empty<DoctorPracticeFeeDto>();
    public bool HasGlobalNexHealthApiKey { get; private set; }
    public string? ErrorMessage { get; set; }
    public string? PracticeFeeMessage { get; set; }
    public bool PracticeFeeSaved { get; set; }
    public string? IntegrationMessage { get; set; }
    public string? AccountMessage { get; set; }
    public bool AccountMessageSuccess { get; set; }
    public bool IntegrationSaved { get; set; }
    public bool ScrollToNexHealth { get; private set; }
    public int HardDeleteWaitDays { get; private set; }
    public bool CanRemoveAccount { get; private set; }
    public DateTime? RemoveAvailableAtUtc { get; private set; }
    public IReadOnlyList<PmsProviderOption> ProviderCandidates { get; set; } = Array.Empty<PmsProviderOption>();

    public class NexHealthIntegrationInput
    {
        public bool IsEnabled { get; set; }
        /// <summary>NexHealth practice subdomain (stored as InstitutionId).</summary>
        public string? Subdomain { get; set; }
        public string? LocationId { get; set; }
        public string? Npi { get; set; }
        public string? ProviderId { get; set; }
        public string? OperatoryId { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int id, bool? saved = null, string? status = null, CancellationToken cancellationToken = default)
    {
        var doctor = await _doctorService.GetForEditAsync(id);
        if (doctor == null) return NotFound();
        Input = doctor;
        IntegrationSaved = saved == true;
        IntegrationMessage = status;
        if (saved == true && !string.IsNullOrWhiteSpace(status)
            && status.Contains("Procedure fee", StringComparison.OrdinalIgnoreCase))
        {
            PracticeFeeSaved = true;
            PracticeFeeMessage = status;
            IntegrationSaved = false;
            IntegrationMessage = null;
        }
        // After save/test/sync redirects land with a status — keep the panel in view.
        ScrollToNexHealth = (saved.HasValue || !string.IsNullOrWhiteSpace(status))
            && !(PracticeFeeSaved);
        await LoadSidePanelsAsync(id, cancellationToken);
        LoadAccountDeletionState();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, IFormFile? photo)
    {
        if (Input.Id == 0)
            Input.Id = id;

        var (success, error) = await _doctorService.UpdateAsync(Input, photo);
        if (!success)
        {
            ErrorMessage = error;
            await LoadSidePanelsAsync(id);
            LoadAccountDeletionState();
            return Page();
        }
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostAddProcedureFeeAsync(int id, CancellationToken cancellationToken = default)
    {
        var doctor = await _doctorService.GetForEditAsync(id, cancellationToken);
        if (doctor == null) return NotFound();
        Input = doctor;

        var (success, error) = await _practiceFeeService.AddAsync(
            id,
            PracticeFeeForm.ProcedureName,
            PracticeFeeForm.FeeUsd,
            cancellationToken);
        if (!success)
        {
            PracticeFeeMessage = error;
            PracticeFeeSaved = false;
            await LoadSidePanelsAsync(id, cancellationToken);
            LoadAccountDeletionState();
            return Page();
        }

        return RedirectToPage(new { id, saved = true, status = "Procedure fee added." });
    }

    public async Task<IActionResult> OnPostUpdateProcedureFeeAsync(int id, CancellationToken cancellationToken = default)
    {
        var doctor = await _doctorService.GetForEditAsync(id, cancellationToken);
        if (doctor == null) return NotFound();
        Input = doctor;

        var (success, error) = await _practiceFeeService.UpdateAsync(
            id,
            PracticeFeeForm.Id,
            PracticeFeeForm.ProcedureName,
            PracticeFeeForm.FeeUsd,
            cancellationToken);
        if (!success)
        {
            PracticeFeeMessage = error;
            PracticeFeeSaved = false;
            await LoadSidePanelsAsync(id, cancellationToken);
            LoadAccountDeletionState();
            return Page();
        }

        return RedirectToPage(new { id, saved = true, status = "Procedure fee updated." });
    }

    public async Task<IActionResult> OnPostDeleteProcedureFeeAsync(int id, int feeId, CancellationToken cancellationToken = default)
    {
        var doctor = await _doctorService.GetForEditAsync(id, cancellationToken);
        if (doctor == null) return NotFound();
        Input = doctor;

        var (success, error) = await _practiceFeeService.DeleteAsync(id, feeId, cancellationToken);
        if (!success)
        {
            PracticeFeeMessage = error;
            PracticeFeeSaved = false;
            await LoadSidePanelsAsync(id, cancellationToken);
            LoadAccountDeletionState();
            return Page();
        }

        return RedirectToPage(new { id, saved = true, status = "Procedure fee deleted." });
    }

    public async Task<IActionResult> OnPostCloseAccountAsync(int id, CancellationToken cancellationToken = default)
    {
        var (success, error) = await _doctorService.SoftDeleteAsync(id, cancellationToken);
        if (!success)
        {
            AccountMessage = error ?? "Unable to close account.";
            AccountMessageSuccess = false;
            var doctor = await _doctorService.GetForEditAsync(id, cancellationToken);
            if (doctor == null) return NotFound();
            Input = doctor;
            await LoadSidePanelsAsync(id, cancellationToken);
            LoadAccountDeletionState();
            return Page();
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostActivateAccountAsync(int id, CancellationToken cancellationToken = default)
    {
        var (success, error) = await _doctorService.ActivateAsync(id, cancellationToken);
        if (!success)
        {
            AccountMessage = error ?? "Unable to activate account.";
            AccountMessageSuccess = false;
            var doctor = await _doctorService.GetForEditAsync(id, cancellationToken);
            if (doctor == null) return NotFound();
            Input = doctor;
            await LoadSidePanelsAsync(id, cancellationToken);
            LoadAccountDeletionState();
            return Page();
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveAccountAsync(int id, CancellationToken cancellationToken = default)
    {
        var (success, error) = await _doctorService.HardDeleteAsync(id, cancellationToken);
        if (!success)
        {
            AccountMessage = error ?? "Unable to remove account.";
            AccountMessageSuccess = false;
            var doctor = await _doctorService.GetForEditAsync(id, cancellationToken);
            if (doctor == null) return NotFound();
            Input = doctor;
            await LoadSidePanelsAsync(id, cancellationToken);
            LoadAccountDeletionState();
            return Page();
        }

        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostSaveNexHealthAsync(int id, CancellationToken cancellationToken = default)
    {
        var doctor = await _doctorService.GetForEditAsync(id);
        if (doctor == null) return NotFound();
        Input = doctor;

        var (success, error, _) = await SaveNexHealthFormAsync(id, cancellationToken);
        if (!success)
        {
            ErrorMessage = error;
            ScrollToNexHealth = true;
            await LoadSidePanelsAsync(id, cancellationToken);
            return Page();
        }

        return RedirectToNexHealth(id, saved: true, status: "NexHealth settings saved.");
    }

    public async Task<IActionResult> OnPostTestNexHealthAsync(int id, CancellationToken cancellationToken = default)
    {
        var doctor = await _doctorService.GetForEditAsync(id);
        if (doctor == null) return NotFound();
        Input = doctor;

        var (saveOk, saveError, _) = await SaveNexHealthFormAsync(id, cancellationToken);
        if (!saveOk)
        {
            ErrorMessage = saveError;
            ScrollToNexHealth = true;
            await LoadSidePanelsAsync(id, cancellationToken);
            return Page();
        }

        var (success, message) = await _pms.TestConnectionAsync(id, PmsProviders.NexHealth, cancellationToken);
        return RedirectToNexHealth(id, saved: success, status: message);
    }

    public async Task<IActionResult> OnPostSyncNexHealthAsync(int id, CancellationToken cancellationToken = default)
    {
        var doctor = await _doctorService.GetForEditAsync(id);
        if (doctor == null) return NotFound();

        var changed = await _pms.SyncInboundForDoctorAsync(id, cancellationToken);
        return RedirectToNexHealth(id, saved: true, status: $"Synced {changed} appointment change(s) from NexHealth.");
    }

    public async Task<IActionResult> OnPostFindProviderByNpiAsync(int id, CancellationToken cancellationToken = default)
    {
        var doctor = await _doctorService.GetForEditAsync(id);
        if (doctor == null) return NotFound();
        Input = doctor;

        var (saveOk, saveError, _) = await SaveNexHealthFormAsync(id, cancellationToken);
        if (!saveOk)
        {
            ErrorMessage = saveError;
            ScrollToNexHealth = true;
            await LoadSidePanelsAsync(id, cancellationToken);
            return Page();
        }

        if (string.IsNullOrWhiteSpace(NexHealthForm.Npi))
        {
            IntegrationMessage = "Enter an NPI number to look up the NexHealth provider ID.";
            ScrollToNexHealth = true;
            await LoadSidePanelsAsync(id, cancellationToken);
            NexHealthForm.Npi = null;
            return Page();
        }

        var (success, message, providerId, candidates) = await _pms.FindNexHealthProviderByNpiAsync(
            id, NexHealthForm.Npi, cancellationToken);

        if (success && !string.IsNullOrWhiteSpace(providerId))
            return RedirectToNexHealth(id, saved: true, status: message);

        IntegrationMessage = message;
        ProviderCandidates = candidates;
        ScrollToNexHealth = true;
        await LoadSidePanelsAsync(id, cancellationToken);
        NexHealthForm.Npi = NexHealthForm.Npi?.Trim();
        return Page();
    }

    private IActionResult RedirectToNexHealth(int id, bool saved, string status)
    {
        var url = Url.Page("./Edit", new { id, saved, status });
        return Redirect($"{url}#nexhealth");
    }

    private async Task<(bool Success, string? Error, PmsConnectionSettingsDto? Connection)> SaveNexHealthFormAsync(
        int id,
        CancellationToken cancellationToken)
    {
        return await _pms.SaveConnectionAsync(id, new PmsConnectionSaveRequest
        {
            Provider = PmsProviders.NexHealth,
            IsEnabled = NexHealthForm.IsEnabled,
            InstitutionId = NexHealthForm.Subdomain,
            LocationExternalId = NexHealthForm.LocationId,
            ProviderExternalId = NexHealthForm.ProviderId,
            OperatoryId = NexHealthForm.OperatoryId
        }, cancellationToken);
    }

    private void LoadAccountDeletionState()
    {
        HardDeleteWaitDays = Math.Max(0, _account.HardDeleteWaitDays);
        CanRemoveAccount = DeletedAccountHelper.CanPermanentlyRemove(Input.DeletedAtUtc, HardDeleteWaitDays);
        RemoveAvailableAtUtc = DeletedAccountHelper.PermanentRemoveAvailableAtUtc(Input.DeletedAtUtc, HardDeleteWaitDays);
    }

    private async Task LoadSidePanelsAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        await LoadNexHealthAsync(doctorId, cancellationToken);
        await LoadPracticeFeesAsync(doctorId, cancellationToken);
    }

    private async Task LoadNexHealthAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        HasGlobalNexHealthApiKey = _pms.HasGlobalNexHealthApiKey;
        NexHealthConnection = await _pms.GetConnectionAsync(doctorId, PmsProviders.NexHealth, cancellationToken);
        if (NexHealthConnection != null)
        {
            NexHealthForm.IsEnabled = NexHealthConnection.IsEnabled;
            NexHealthForm.Subdomain = NexHealthConnection.InstitutionId;
            NexHealthForm.LocationId = NexHealthConnection.LocationExternalId;
            NexHealthForm.ProviderId = NexHealthConnection.ProviderExternalId;
            NexHealthForm.OperatoryId = NexHealthConnection.OperatoryId;
        }
    }

    private async Task LoadPracticeFeesAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        PracticeFees = await _practiceFeeService.ListAsync(doctorId, cancellationToken);
    }
}
