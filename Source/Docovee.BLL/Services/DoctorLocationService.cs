using Docovee.BLL.Data;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IDoctorLocationService
{
    Task<IReadOnlyList<DoctorLocationDto>> GetLocationsAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DoctorLocationInput>> GetAllLocationInputsAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<DoctorLocationInput?> GetLocationForEditAsync(int doctorId, int locationId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> AddLocationsAsync(int doctorId, IReadOnlyList<DoctorLocationInput> locations, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdateLocationAsync(int doctorId, DoctorLocationInput input, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> DeleteLocationAsync(int doctorId, int locationId, CancellationToken cancellationToken = default);
}

public class DoctorLocationService : IDoctorLocationService
{
    private readonly DocoveeDbContext _db;

    public DoctorLocationService(DocoveeDbContext db) => _db = db;

    public async Task<IReadOnlyList<DoctorLocationDto>> GetLocationsAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedFromDoctorProfileAsync(doctorId, cancellationToken);

        var rows = await _db.DoctorLocations.AsNoTracking()
            .Where(l => l.DoctorId == doctorId)
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.Id)
            .ToListAsync(cancellationToken);

        return rows.Select(MapToDto).ToList();
    }

    public async Task<IReadOnlyList<DoctorLocationInput>> GetAllLocationInputsAsync(int doctorId, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedFromDoctorProfileAsync(doctorId, cancellationToken);

        var rows = await _db.DoctorLocations.AsNoTracking()
            .Where(l => l.DoctorId == doctorId)
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.Id)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new DoctorLocationInput
        {
            Id = r.Id,
            Name = r.Name,
            InPerson = r.InPerson,
            VideoVisits = r.VideoVisits,
            Address1 = r.Address1,
            Address2 = r.Address2,
            City = r.City,
            State = r.State,
            ZipCode = r.ZipCode,
            PhoneNumber = r.PhoneNumber,
            PhoneExt = r.PhoneExt,
            Fax = r.Fax,
            ContactPersonName = r.ContactPersonName,
            AppointmentNotificationEmail = r.AppointmentNotificationEmail
        }).ToList();
    }

    public async Task<DoctorLocationInput?> GetLocationForEditAsync(int doctorId, int locationId, CancellationToken cancellationToken = default)
    {
        var row = await _db.DoctorLocations.AsNoTracking()
            .FirstOrDefaultAsync(l => l.DoctorId == doctorId && l.Id == locationId, cancellationToken);
        if (row == null) return null;

        return new DoctorLocationInput
        {
            Id = row.Id,
            Name = row.Name,
            InPerson = row.InPerson,
            VideoVisits = row.VideoVisits,
            Address1 = row.Address1,
            Address2 = row.Address2,
            City = row.City,
            State = row.State,
            ZipCode = row.ZipCode,
            PhoneNumber = row.PhoneNumber,
            PhoneExt = row.PhoneExt,
            Fax = row.Fax,
            ContactPersonName = row.ContactPersonName,
            AppointmentNotificationEmail = row.AppointmentNotificationEmail
        };
    }

    public async Task<(bool Success, string? Error)> AddLocationsAsync(
        int doctorId,
        IReadOnlyList<DoctorLocationInput> locations,
        CancellationToken cancellationToken = default)
    {
        if (locations.Count == 0)
            return (false, "Add at least one location.");

        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return (false, "Doctor not found.");

        var existingCount = await _db.DoctorLocations.CountAsync(l => l.DoctorId == doctorId, cancellationToken);
        var hadAny = existingCount > 0;
        var sortOrder = existingCount;
        DoctorLocation? firstPrimary = null;

        foreach (var input in locations)
        {
            var error = ValidateInput(input);
            if (error != null)
                return (false, error);

            var entity = MapToEntity(input, doctorId, sortOrder++, !hadAny && firstPrimary == null);
            _db.DoctorLocations.Add(entity);
            if (entity.IsPrimary)
                firstPrimary = entity;
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (firstPrimary != null)
            await SyncPrimaryToDoctorAsync(doctor, firstPrimary, cancellationToken);

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateLocationAsync(
        int doctorId,
        DoctorLocationInput input,
        CancellationToken cancellationToken = default)
    {
        if (input.Id is not int locationId || locationId <= 0)
            return (false, "Location not found.");

        var error = ValidateInput(input);
        if (error != null)
            return (false, error);

        var entity = await _db.DoctorLocations
            .Include(l => l.Doctor)
            .FirstOrDefaultAsync(l => l.DoctorId == doctorId && l.Id == locationId, cancellationToken);
        if (entity == null)
            return (false, "Location not found.");

        ApplyInput(entity, input);
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        if (entity.IsPrimary)
            await SyncPrimaryToDoctorAsync(entity.Doctor, entity, cancellationToken);

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteLocationAsync(
        int doctorId,
        int locationId,
        CancellationToken cancellationToken = default)
    {
        var entity = await _db.DoctorLocations
            .FirstOrDefaultAsync(l => l.DoctorId == doctorId && l.Id == locationId, cancellationToken);
        if (entity == null)
            return (false, "Location not found.");

        var wasPrimary = entity.IsPrimary;
        _db.DoctorLocations.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);

        if (wasPrimary)
        {
            var next = await _db.DoctorLocations
                .Include(l => l.Doctor)
                .Where(l => l.DoctorId == doctorId)
                .OrderBy(l => l.SortOrder)
                .ThenBy(l => l.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (next != null)
            {
                next.IsPrimary = true;
                next.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(cancellationToken);
                await SyncPrimaryToDoctorAsync(next.Doctor, next, cancellationToken);
            }
        }

        return (true, null);
    }

    private async Task EnsureMigratedFromDoctorProfileAsync(int doctorId, CancellationToken cancellationToken)
    {
        var hasLocations = await _db.DoctorLocations.AnyAsync(l => l.DoctorId == doctorId, cancellationToken);
        if (hasLocations)
            return;

        var doctor = await _db.Doctors.AsNoTracking().FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return;

        var hasAddress = !string.IsNullOrWhiteSpace(doctor.Address)
            || !string.IsNullOrWhiteSpace(doctor.City)
            || !string.IsNullOrWhiteSpace(doctor.State)
            || !string.IsNullOrWhiteSpace(doctor.ZipCode);

        if (!hasAddress && string.IsNullOrWhiteSpace(doctor.OfficePhoneNumber))
            return;

        var location = new DoctorLocation
        {
            DoctorId = doctorId,
            Name = string.IsNullOrWhiteSpace(doctor.PracticeName) ? doctor.Name : doctor.PracticeName,
            InPerson = true,
            VideoVisits = false,
            Address1 = doctor.Address?.Trim() ?? string.Empty,
            City = doctor.City?.Trim() ?? string.Empty,
            State = UsStates.Normalize(doctor.State) ?? doctor.State?.Trim() ?? string.Empty,
            ZipCode = doctor.ZipCode?.Trim() ?? string.Empty,
            PhoneNumber = doctor.OfficePhoneNumber?.Trim() ?? string.Empty,
            AppointmentNotificationEmail = doctor.Username,
            IsActive = doctor.IsActive,
            IsPrimary = true,
            SortOrder = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.DoctorLocations.Add(location);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string? ValidateInput(DoctorLocationInput input)
    {
        if (!input.InPerson && !input.VideoVisits)
            return "Select at least one location type (In person or Video visits).";

        if (input.InPerson)
        {
            if (string.IsNullOrWhiteSpace(input.Address1))
                return "Address is required for in-person locations.";
            if (string.IsNullOrWhiteSpace(input.City))
                return "City is required for in-person locations.";
            if (string.IsNullOrWhiteSpace(input.State) || !UsStates.IsValid(input.State))
                return "Select a valid state.";
            if (string.IsNullOrWhiteSpace(input.ZipCode))
                return "Zip code is required for in-person locations.";
        }

        if (string.IsNullOrWhiteSpace(input.PhoneNumber))
            return "Practice phone number is required.";

        if (!string.IsNullOrWhiteSpace(input.AppointmentNotificationEmail)
            && !input.AppointmentNotificationEmail.Contains('@'))
            return "Enter a valid email for appointment notifications.";

        return null;
    }

    private static DoctorLocation MapToEntity(DoctorLocationInput input, int doctorId, int sortOrder, bool isFirst)
    {
        var state = UsStates.Normalize(input.State) ?? input.State?.Trim() ?? string.Empty;
        return new DoctorLocation
        {
            DoctorId = doctorId,
            Name = string.IsNullOrWhiteSpace(input.Name) ? null : input.Name.Trim(),
            InPerson = input.InPerson,
            VideoVisits = input.VideoVisits,
            Address1 = input.Address1?.Trim() ?? string.Empty,
            Address2 = string.IsNullOrWhiteSpace(input.Address2) ? null : input.Address2.Trim(),
            City = input.City?.Trim() ?? string.Empty,
            State = state,
            ZipCode = input.ZipCode?.Trim() ?? string.Empty,
            PhoneNumber = input.PhoneNumber!.Trim(),
            PhoneExt = string.IsNullOrWhiteSpace(input.PhoneExt) ? null : input.PhoneExt.Trim(),
            Fax = string.IsNullOrWhiteSpace(input.Fax) ? null : input.Fax.Trim(),
            ContactPersonName = string.IsNullOrWhiteSpace(input.ContactPersonName) ? null : input.ContactPersonName.Trim(),
            AppointmentNotificationEmail = string.IsNullOrWhiteSpace(input.AppointmentNotificationEmail)
                ? null
                : input.AppointmentNotificationEmail.Trim(),
            IsActive = true,
            IsPrimary = isFirst,
            SortOrder = sortOrder,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static void ApplyInput(DoctorLocation entity, DoctorLocationInput input)
    {
        entity.Name = string.IsNullOrWhiteSpace(input.Name) ? null : input.Name.Trim();
        entity.InPerson = input.InPerson;
        entity.VideoVisits = input.VideoVisits;
        entity.Address1 = input.Address1?.Trim() ?? string.Empty;
        entity.Address2 = string.IsNullOrWhiteSpace(input.Address2) ? null : input.Address2.Trim();
        entity.City = input.City?.Trim() ?? string.Empty;
        entity.State = UsStates.Normalize(input.State) ?? input.State?.Trim() ?? string.Empty;
        entity.ZipCode = input.ZipCode?.Trim() ?? string.Empty;
        entity.PhoneNumber = input.PhoneNumber!.Trim();
        entity.PhoneExt = string.IsNullOrWhiteSpace(input.PhoneExt) ? null : input.PhoneExt.Trim();
        entity.Fax = string.IsNullOrWhiteSpace(input.Fax) ? null : input.Fax.Trim();
        entity.ContactPersonName = string.IsNullOrWhiteSpace(input.ContactPersonName) ? null : input.ContactPersonName.Trim();
        entity.AppointmentNotificationEmail = string.IsNullOrWhiteSpace(input.AppointmentNotificationEmail)
            ? null
            : input.AppointmentNotificationEmail.Trim();
    }

    private async Task SyncPrimaryToDoctorAsync(Doctor doctor, DoctorLocation primary, CancellationToken cancellationToken)
    {
        doctor.Address = string.Join(", ", new[] { primary.Address1, primary.Address2 }.Where(x => !string.IsNullOrWhiteSpace(x)));
        doctor.City = primary.City;
        doctor.State = primary.State;
        doctor.ZipCode = primary.ZipCode;
        doctor.OfficePhoneNumber = FormatPhone(primary.PhoneNumber, primary.PhoneExt);
        if (!string.IsNullOrWhiteSpace(primary.Name))
            doctor.PracticeName = primary.Name;
        doctor.Location = $"{primary.City}, {primary.State}";
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static DoctorLocationDto MapToDto(DoctorLocation row)
    {
        var cityStateZip = string.Join(" ", new[]
        {
            string.IsNullOrWhiteSpace(row.City) && string.IsNullOrWhiteSpace(row.State)
                ? null
                : $"{row.City}{(string.IsNullOrWhiteSpace(row.City) || string.IsNullOrWhiteSpace(row.State) ? "" : ", ")}{row.State}".Trim().Trim(','),
            row.ZipCode
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

        var street = string.Join(", ", new[] { row.Address1, row.Address2 }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var formattedAddress = string.Join(", ", new[] { street, cityStateZip }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(formattedAddress))
            formattedAddress = "—";

        var types = new List<string>();
        if (row.InPerson) types.Add("In-person");
        if (row.VideoVisits) types.Add("Video visits");
        var typeLabel = types.Count > 0 ? string.Join(", ", types) : "—";

        return new DoctorLocationDto
        {
            Id = row.Id,
            DisplayName = string.IsNullOrWhiteSpace(row.Name) ? "—" : row.Name,
            IsActive = row.IsActive,
            FormattedAddress = formattedAddress,
            LocationTypeLabel = typeLabel,
            PhoneDisplay = string.IsNullOrWhiteSpace(row.PhoneNumber) ? "—" : FormatPhone(row.PhoneNumber, row.PhoneExt),
            EmailDisplay = string.IsNullOrWhiteSpace(row.AppointmentNotificationEmail) ? "—" : row.AppointmentNotificationEmail
        };
    }

    private static string FormatPhone(string phone, string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
            return phone;
        return $"{phone} ext. {ext.Trim()}";
    }
}
