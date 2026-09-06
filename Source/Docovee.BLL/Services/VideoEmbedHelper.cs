using System.Text.RegularExpressions;

namespace Docovee.BLL.Services;

/// <summary>Converts watch/share video links into iframe-safe embed URLs.</summary>
public static class VideoEmbedHelper
{
    /// <summary>
    /// Returns an embed URL for YouTube or Vimeo, or null if the input is empty/unrecognized.
    /// Watch and share URLs (e.g. youtube.com/watch?v=…) cannot be loaded in an iframe.
    /// </summary>
    public static string? ToEmbedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.Trim();

        var yt = Regex.Match(
            trimmed,
            @"(?:youtube\.com\/watch\?v=|youtu\.be\/|youtube\.com\/embed\/|youtube\.com\/shorts\/)([\w-]+)",
            RegexOptions.IgnoreCase);
        if (yt.Success)
            return $"https://www.youtube.com/embed/{yt.Groups[1].Value}";

        var vimeo = Regex.Match(
            trimmed,
            @"(?:player\.)?vimeo\.com\/(?:video\/)?(\d+)",
            RegexOptions.IgnoreCase);
        if (vimeo.Success)
            return $"https://player.vimeo.com/video/{vimeo.Groups[1].Value}";

        return null;
    }
}
