namespace Docovee.BLL.Services;

public static class DoctorPhotoHelper
{
    public static bool IsValidImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (trimmed.Contains(' ') && !trimmed.Contains('%'))
            return false;

        return uri.Host.Length > 0;
    }

    public static bool IsLocalUploadPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.TrimStart().StartsWith('/');

    public static string? GetDisplayPhotoUrl(string? photoUrl, string? gmbPhotoLink)
    {
        if (IsValidImageUrl(photoUrl))
            return photoUrl!.Trim();

        if (IsLocalUploadPath(photoUrl))
            return photoUrl!.Trim();

        if (IsValidImageUrl(gmbPhotoLink))
            return gmbPhotoLink!.Trim();

        return null;
    }

    public static string? NormalizeStoredLink(string? url) =>
        IsValidImageUrl(url) ? url!.Trim() : null;
}
