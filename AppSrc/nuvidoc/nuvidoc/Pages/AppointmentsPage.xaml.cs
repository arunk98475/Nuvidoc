using System.Windows.Input;
using Docovee.DS.Models;
using nuvidoc.Services;

namespace nuvidoc;

public partial class AppointmentsPage : ContentPage
{
    private readonly NuvidocApiClient _api;
    private bool _loaded;

    public AppointmentsPage(NuvidocApiClient api)
    {
        InitializeComponent();
        _api = api;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded || AuthSession.IsSignedIn)
            await LoadAsync();
        _loaded = true;
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        try { await LoadAsync(); }
        finally { Refresh.IsRefreshing = false; }
    }

    private async Task LoadAsync()
    {
        if (!AuthSession.IsSignedIn)
        {
            List.ItemsSource = Array.Empty<AppointmentRow>();
            return;
        }

        try
        {
            var data = await _api.GetAppointmentsAsync();
            if (data == null)
                return;

            List.ItemsSource = data.Upcoming
                .Select(a => new AppointmentRow(a, async id => await CancelAsync(id)))
                .ToList();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Appointments", ex.Message, "OK");
        }
    }

    private async Task CancelAsync(int appointmentId)
    {
        var confirm = await DisplayAlert(
            "Cancel appointment",
            "Nuvi will call the office to confirm cancellation. Continue?",
            "Yes",
            "No");
        if (!confirm)
            return;

        try
        {
            var result = await _api.CancelAppointmentAsync(appointmentId);
            if (result == null)
            {
                await DisplayAlert("Cancel", "Please sign in again.", "OK");
                return;
            }

            await DisplayAlert(result.Success ? "Cancel" : "Could not cancel", result.Message, "OK");
            if (result.Success)
                await LoadAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Cancel", ex.Message, "OK");
        }
    }

    private sealed class AppointmentRow
    {
        public AppointmentRow(PatientAppointmentDto dto, Func<int, Task> cancel)
        {
            AppointmentId = dto.Id;
            DoctorName = dto.DoctorName;
            SlotLabel = dto.StartsAt.ToLocalTime().ToString("ddd, MMM d · h:mm tt");
            Details = $"{dto.VisitReason} · {dto.Status}";
            CancelCommand = new Command(async () => await cancel(AppointmentId));
        }

        public int AppointmentId { get; }
        public string DoctorName { get; }
        public string SlotLabel { get; }
        public string Details { get; }
        public ICommand CancelCommand { get; }
    }
}
