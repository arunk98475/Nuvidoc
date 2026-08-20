using Docovee.BLL.Configuration;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public interface IDoctorMediaService
{
    Task<IReadOnlyList<DoctorMediaDto>> GetForDoctorAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<long> GetVideoBytesUsedAsync(int doctorId, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> AddPhotoAsync(
        int doctorId,
        string mediaType,
        IFormFile file,
        string? caption = null,
        CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> AddVideoAsync(
        int doctorId,
        IFormFile file,
        string? caption = null,
        CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> AddYoutubeVideoAsync(
        int doctorId,
        string youtubeUrl,
        string? caption = null,
        CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> DeleteAsync(int doctorId, int mediaId, CancellationToken cancellationToken = default);
}

public class DoctorMediaService : IDoctorMediaService
{
    private const int MaxPhotosPerType = 24;
    private const int MaxVideosPerDoctor = 24;

    private readonly DocoveeDbContext _db;
    private readonly IDoctorFileService _fileService;
    private readonly UploadOptions _uploadOptions;
    private readonly IDoctorQualityScoreService _qualityScore;

    public DoctorMediaService(
        DocoveeDbContext db,
        IDoctorFileService fileService,
        IOptions<UploadOptions> uploadOptions,
        IDoctorQualityScoreService qualityScore)
    {
        _db = db;
        _fileService = fileService;
        _uploadOptions = uploadOptions.Value;
        _qualityScore = qualityScore;
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
                FileSizeBytes = m.FileSizeBytes,
                SortOrder = m.SortOrder
            })
            .ToListAsync(cancellationToken);

    public async Task<long> GetVideoBytesUsedAsync(int doctorId, CancellationToken cancellationToken = default) =>
        await _db.DoctorMedia.AsNoTracking()
            .Where(m => m.DoctorId == doctorId
                        && m.MediaType == DoctorMediaTypes.Video
                        && m.FileSizeBytes > 0)
            .SumAsync(m => (long?)m.FileSizeBytes, cancellationToken) ?? 0L;

    public async Task<(bool Success, string? Error)> AddPhotoAsync(
        int doctorId,
        string mediaType,
        IFormFile file,
        string? caption = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _db.Doctors.AnyAsync(d => d.Id == doctorId, cancellationToken))
            return (false, "Doctor not found.");

        if (!DoctorMediaTypes.IsPhotoType(mediaType))
            return (false, "Choose a valid gallery type (Clinic, Team, Smile, Family, or Pets).");

        var normalizedType = DoctorMediaTypes.NormalizePhotoType(mediaType);

        if (file == null || file.Length == 0)
            return (false, "Please choose a photo to upload.");

        var count = await _db.DoctorMedia.CountAsync(
            m => m.DoctorId == doctorId && m.MediaType == normalizedType, cancellationToken);
        if (count >= MaxPhotosPerType)
            return (false, $"You can upload up to {MaxPhotosPerType} {normalizedType.ToLowerInvariant()} photos.");

        var url = await _fileService.SaveUploadedPhotoAsync(doctorId, file, cancellationToken);
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
            Caption = TruncateCaption(caption),
            FileSizeBytes = file.Length,
            SortOrder = maxSort + 1,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
        await _qualityScore.RecomputeAndPersistAsync(doctorId, cancellationToken);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> AddVideoAsync(
        int doctorId,
        IFormFile file,
        string? caption = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _db.Doctors.AnyAsync(d => d.Id == doctorId, cancellationToken))
            return (false, "Doctor not found.");

        if (file == null || file.Length == 0)
            return (false, "Please choose a video to upload.");

        var usedBytes = await GetVideoBytesUsedAsync(doctorId, cancellationToken);
        var maxBytes = _uploadOptions.MaxUploadBytes;
        var remaining = maxBytes - usedBytes;
        if (remaining <= 0)
            return (false, $"Video storage is full ({_uploadOptions.MaxUploadMb} MB total for this profile). Remove a video to upload another.");

        if (file.Length > remaining)
        {
            var remainingMb = Math.Max(1, (int)(remaining / (1024L * 1024L)));
            return (false, $"This video exceeds the remaining space ({remainingMb} MB of {_uploadOptions.MaxUploadMb} MB total left).");
        }

        var url = await _fileService.SaveUploadedVideoAsync(doctorId, file, remaining, cancellationToken);
        if (string.IsNullOrWhiteSpace(url))
            return (false, $"Could not save that video. Use mp4, webm, mov, ogg, or m4v (up to {_uploadOptions.MaxUploadMb} MB total).");

        var count = await _db.DoctorMedia.CountAsync(
            m => m.DoctorId == doctorId && m.MediaType == DoctorMediaTypes.Video, cancellationToken);
        if (count >= MaxVideosPerDoctor)
            return (false, $"You can add up to {MaxVideosPerDoctor} videos.");

        var maxSort = await _db.DoctorMedia
            .Where(m => m.DoctorId == doctorId && m.MediaType == DoctorMediaTypes.Video)
            .Select(m => (int?)m.SortOrder)
            .MaxAsync(cancellationToken) ?? -1;

        _db.DoctorMedia.Add(new DoctorMedia
        {
            DoctorId = doctorId,
            MediaType = DoctorMediaTypes.Video,
            Url = url,
            Caption = TruncateCaption(caption),
            FileSizeBytes = file.Length,
            SortOrder = maxSort + 1,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
        await _qualityScore.RecomputeAndPersistAsync(doctorId, cancellationToken);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> AddYoutubeVideoAsync(
        int doctorId,
        string youtubeUrl,
        string? caption = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _db.Doctors.AnyAsync(d => d.Id == doctorId, cancellationToken))
            return (false, "Doctor not found.");

        if (!TryNormalizeYoutubeUrl(youtubeUrl, out var normalizedUrl, out var error))
            return (false, error);

        var count = await _db.DoctorMedia.CountAsync(
            m => m.DoctorId == doctorId && m.MediaType == DoctorMediaTypes.Video, cancellationToken);
        if (count >= MaxVideosPerDoctor)
            return (false, $"You can add up to {MaxVideosPerDoctor} videos.");

        var alreadyExists = await _db.DoctorMedia.AnyAsync(
            m => m.DoctorId == doctorId
                 && m.MediaType == DoctorMediaTypes.Video
                 && m.Url == normalizedUrl,
            cancellationToken);
        if (alreadyExists)
            return (false, "That YouTube video is already on your profile.");

        var maxSort = await _db.DoctorMedia
            .Where(m => m.DoctorId == doctorId && m.MediaType == DoctorMediaTypes.Video)
            .Select(m => (int?)m.SortOrder)
            .MaxAsync(cancellationToken) ?? -1;

        _db.DoctorMedia.Add(new DoctorMedia
        {
            DoctorId = doctorId,
            MediaType = DoctorMediaTypes.Video,
            Url = normalizedUrl,
            Caption = TruncateCaption(caption),
            FileSizeBytes = 0,
            SortOrder = maxSort + 1,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
        await _qualityScore.RecomputeAndPersistAsync(doctorId, cancellationToken);
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
            return (false, "Media not found.");

        _db.DoctorMedia.Remove(media);
        await _db.SaveChangesAsync(cancellationToken);
        await _qualityScore.RecomputeAndPersistAsync(doctorId, cancellationToken);
        return (true, null);
    }

    private static string? TruncateCaption(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
            return null;
        var trimmed = caption.Trim();
        return trimmed[..Math.Min(trimmed.Length, 300)];
    }

    private static bool TryNormalizeYoutubeUrl(string? url, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "Please enter a YouTube video link.";
            return false;
        }

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            error = "Please enter a valid YouTube video link (youtube.com or youtu.be).";
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host is not ("youtube.com" or "www.youtube.com" or "m.youtube.com" or "youtu.be" or "www.youtu.be"))
        {
            error = "Please enter a valid YouTube video link (youtube.com or youtu.be).";
            return false;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            trimmed,
            @"(?:youtube\.com\/watch\?v=|youtu\.be\/|youtube\.com\/embed\/|youtube\.com\/shorts\/)([\w-]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            error = "Please enter a full YouTube video link (for example https://www.youtube.com/watch?v=...).";
            return false;
        }

        normalized = $"https://www.youtube.com/watch?v={match.Groups[1].Value}";
        return true;
    }
}
