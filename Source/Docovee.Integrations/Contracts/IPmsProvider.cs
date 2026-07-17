namespace Docovee.Integrations.Contracts;

public interface IPmsProvider
{
    string ProviderId { get; }

    Task<PmsConnectionResult> TestConnectionAsync(
        PmsConnectionCredentials credentials,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PmsSlot>> GetAvailabilityAsync(
        PmsAvailabilityRequest request,
        CancellationToken cancellationToken = default);

    Task<PmsAppointmentResult> CreateAppointmentAsync(
        PmsCreateAppointmentRequest request,
        CancellationToken cancellationToken = default);

    Task<PmsAppointmentResult> UpdateAppointmentAsync(
        PmsUpdateAppointmentRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PmsExternalAppointment>> PullRecentAppointmentsAsync(
        PmsPullChangesRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create or resolve a PMS provider record for a NuviDoc doctor and return its external id.
    /// </summary>
    Task<PmsProviderEnsureResult> EnsureProviderAsync(
        PmsEnsureProviderRequest request,
        CancellationToken cancellationToken = default);
}
