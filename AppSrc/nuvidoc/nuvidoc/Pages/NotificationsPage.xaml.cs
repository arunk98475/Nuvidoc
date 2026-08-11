using Docovee.DS.Models;
using nuvidoc.Services;

namespace nuvidoc;

public partial class NotificationsPage : ContentPage
{
    private readonly NuvidocApiClient _api;
    private readonly BookingAlertHub _alerts;
    private bool _loaded;

    public NotificationsPage(NuvidocApiClient api, BookingAlertHub alerts)
    {
        InitializeComponent();
        _api = api;
        _alerts = alerts;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _alerts.Received += OnAlert;
        if (!_loaded || AuthSession.IsSignedIn)
            await LoadAsync();
        _loaded = true;
    }

    protected override void OnDisappearing()
    {
        _alerts.Received -= OnAlert;
        base.OnDisappearing();
    }

    private async Task OnAlert(PatientPushMessage _)
    {
        await MainThread.InvokeOnMainThreadAsync(LoadAsync);
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
            UnreadLabel.Text = "Sign in to see updates";
            List.ItemsSource = Array.Empty<NotificationRow>();
            return;
        }

        try
        {
            var data = await _api.GetNotificationsAsync();
            if (data == null)
            {
                UnreadLabel.Text = "Unable to load";
                return;
            }

            UnreadLabel.Text = data.UnreadCount > 0 ? $"{data.UnreadCount} unread" : "All caught up";
            List.ItemsSource = data.Items.Select(n => new NotificationRow(n)).ToList();
            if (data.UnreadCount > 0)
                await _api.MarkNotificationsReadAsync();
        }
        catch (Exception ex)
        {
            UnreadLabel.Text = ex.Message;
        }
    }

    private sealed class NotificationRow
    {
        public NotificationRow(MobileNotificationDto n)
        {
            Title = n.Title;
            Body = n.Body;
            Timestamp = n.CreatedAt.ToLocalTime().ToString("MMM d · h:mm tt");

            if (!string.IsNullOrWhiteSpace(n.SlotLabel))
                AppointmentSlot = $"Appointment · {n.SlotLabel}";
            else if (n.AppointmentStartsAt is DateTime start)
            {
                AppointmentSlot =
                    $"Appointment · {start.ToLocalTime():ddd, MMM d · h:mm tt}";
            }
            else
                AppointmentSlot = string.Empty;
        }

        public string Title { get; }
        public string Body { get; }
        public string Timestamp { get; }
        public string AppointmentSlot { get; }
        public bool HasAppointmentSlot => !string.IsNullOrWhiteSpace(AppointmentSlot);
    }
}
