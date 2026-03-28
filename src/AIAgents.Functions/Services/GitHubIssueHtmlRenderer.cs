using System.Net;
using System.Text.RegularExpressions;

namespace AIAgents.Functions.Services;

internal static class GitHubIssueHtmlRenderer
{
    private static readonly Regex s_imgTagRegex = new(
        "<img(?<attrs>[^>]*)src=[\"'](?<src>[^\"']+)[\"'](?<tail>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_altRegex = new(
        "alt=[\"'](?<alt>[^\"']*)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string RewriteAdoHtml(
        string html,
        string? gitHubOwner,
        string? gitHubRepo,
        string? branchName,
        IReadOnlyList<string>? uploadedImagePaths,
        bool supportingArtifactsInBranch)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return html;
        }

        if (!supportingArtifactsInBranch ||
            string.IsNullOrWhiteSpace(gitHubOwner) ||
            string.IsNullOrWhiteSpace(gitHubRepo) ||
            string.IsNullOrWhiteSpace(branchName) ||
            uploadedImagePaths is null ||
            uploadedImagePaths.Count == 0)
        {
            return s_imgTagRegex.Replace(html, match => BuildFallbackImageMarkup(match.Groups["src"].Value, match.Value));
        }

        var remainingPaths = new List<string>(uploadedImagePaths);
        return s_imgTagRegex.Replace(html, match =>
        {
            var src = WebUtility.HtmlDecode(match.Groups["src"].Value);
            var originalMarkup = match.Value;
            var candidate = TryMatchImagePath(src, remainingPaths);
            if (candidate is null)
            {
                return BuildFallbackImageMarkup(src, originalMarkup);
            }

            remainingPaths.Remove(candidate);

            var rawUrl = BuildBranchFileUrl(gitHubOwner, gitHubRepo, branchName, candidate);
            var altMatch = s_altRegex.Match(originalMarkup);
            var alt = altMatch.Success && !string.IsNullOrWhiteSpace(altMatch.Groups["alt"].Value)
                ? WebUtility.HtmlEncode(altMatch.Groups["alt"].Value)
                : "Story image";

            return $"<img src=\"{rawUrl}\" alt=\"{alt}\">";
        });
    }

    private static string? TryMatchImagePath(string src, IReadOnlyList<string> candidatePaths)
    {
        if (candidatePaths.Count == 0)
        {
            return null;
        }

        var fileName = GetFileNameFromUrl(src);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return candidatePaths.Count == 1 ? candidatePaths[0] : null;
        }

        var exact = candidatePaths.FirstOrDefault(path =>
            string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return candidatePaths.FirstOrDefault(path =>
        {
            var pathFileName = Path.GetFileName(path);
            if (!pathFileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var pathStem = Path.GetFileNameWithoutExtension(pathFileName);
            return pathStem.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                   pathStem.StartsWith($"{stem}-", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string BuildBranchFileUrl(string owner, string repo, string branchName, string relativePath)
    {
        var encodedBranch = Uri.EscapeDataString(branchName).Replace("%2F", "/");
        var encodedPath = string.Join("/",
            relativePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));

        return $"https://github.com/{owner}/{repo}/blob/{encodedBranch}/{encodedPath}?raw=1";
    }

    private static string BuildFallbackImageMarkup(string src, string originalMarkup)
    {
        var altMatch = s_altRegex.Match(originalMarkup);
        var altText = altMatch.Success && !string.IsNullOrWhiteSpace(altMatch.Groups["alt"].Value)
            ? WebUtility.HtmlEncode(altMatch.Groups["alt"].Value)
            : "Story image";

        var encodedSrc = WebUtility.HtmlEncode(src);
        return $"<p><em>{altText} available in Azure DevOps.</em> <a href=\"{encodedSrc}\">Open original image</a></p>";
    }

    private static string? GetFileNameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Path.GetFileName(url);
        }

        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length != 2)
            {
                continue;
            }

            if (string.Equals(pieces[0], "fileName", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pieces[1]);
            }
        }

        return Path.GetFileName(uri.LocalPath);
    }
}
