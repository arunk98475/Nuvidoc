using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IDoctorMediaService
{
    Task<IReadOnlyList<DoctorMediaDto>> GetForDoctorAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> AddPhotoAsync(
        int doctorId,
        string mediaType,
        IFormFile file,
        string? caption = null,
        CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> DeleteAsync(int doctorId, int mediaId, CancellationToken cancellationToken = default);
}

public class DoctorMediaService : IDoctorMediaService
{
    private const int MaxPhotosPerType = 24;

    private readonly DocoveeDbContext _db;
    private readonly IDoctorFileService _fileService;

    public DoctorMediaService(DocoveeDbContext db, IDoctorFileService fileService)
    {
        _db = db;
        _fileService = fileService;
    }

    public async Task<IReadOnlyList<DoctorMediaDto>> GetForDoctorAsync(
        int doctorId,
        CancellationToken cancellationToken = default) =>
        await _db.DoctorMedia.AsNoTracking()
            .Where(m => m.DoctorId == doctorId)
            .OrderBy(m => m.MediaType)
            .ThenBy(m => m.SortOrder)
            .ThenBy(m => m.Id)
            .Select(m => new DoctorMediaDto
            {
                Id = m.Id,
                MediaType = m.MediaType,
                Url = m.Url,
                Caption = m.Caption,
                SortOrder = m.SortOrder
            })
            .ToListAsync(cancellationToken);

    public async Task<(bool Success, string? Error)> AddPhotoAsync(
        int doctorId,
        string mediaType,
        IFormFile file,
        string? caption = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _db.Doctors.AnyAsync(d => d.Id == doctorId, cancellationToken))
            return (false, "Doctor not found.");

        if (!DoctorMediaTypes.IsValid(mediaType))
            return (false, "Choose a valid gallery type (Clinic, Team, Smile, Family, or Pets).");

        var normalizedType = DoctorMediaTypes.Normalize(mediaType);

        if (file == null || file.Length == 0)
            return (false, "Please choose a photo to upload.");

        var count = await _db.DoctorMedia.CountAsync(
            m => m.DoctorId == doctorId && m.MediaType == normalizedType, cancellationToken);
        if (count >= MaxPhotosPerType)
            return (false, $"You can upload up to {MaxPhotosPerType} {normalizedType.ToLowerInvariant()} photos.");

        var url = await _fileService.SaveUploadedPhotoAsync(file, cancellationToken);
        if (string.IsNullOrWhiteSpace(url))
            return (false, "Could not save that image. Use JPG, PNG, WebP, or GIF.");

        var maxSort = await _db.DoctorMedia
            .Where(m => m.DoctorId == doctorId && m.MediaType == normalizedType)
            .Select(m => (int?)m.SortOrder)
            .MaxAsync(cancellationToken) ?? -1;

        _db.DoctorMedia.Add(new DoctorMedia
        {
            DoctorId = doctorId,
            MediaType = normalizedType,
            Url = url,
            Caption = string.IsNullOrWhiteSpace(caption)
                ? null
                : caption.Trim()[..Math.Min(caption.Trim().Length, 300)],
            SortOrder = maxSort + 1,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteAsync(
        int doctorId,
        int mediaId,
        CancellationToken cancellationToken = default)
    {
        var media = await _db.DoctorMedia
            .FirstOrDefaultAsync(m => m.Id == mediaId && m.DoctorId == doctorId, cancellationToken);
        if (media == null)
            return (false, "Photo not found.");

        _db.DoctorMedia.Remove(media);
        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }
}
