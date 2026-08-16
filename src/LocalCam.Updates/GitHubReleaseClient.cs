using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalCam.Updates;

public sealed record GitHubRelease(
    Version Version,
    string TagName,
    Uri ReleasePage,
    Uri InstallerDownload);

public sealed record UpdateCheckResult(
    Version CurrentVersion,
    GitHubRelease LatestRelease)
{
    public bool IsUpdateAvailable => LatestRelease.Version > CurrentVersion;
}

public sealed partial class GitHubReleaseClient(HttpClient httpClient)
{
    public const string InstallerAssetName = "LocalCam-Setup-x64.exe";

    public async Task<UpdateCheckResult> CheckLatestAsync(
        string repository,
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (!RepositoryPattern().IsMatch(repository))
        {
            throw new ArgumentException("GitHub 仓库应使用 owner/repository 格式。", nameof(repository));
        }

        var normalizedCurrent = Normalize(currentVersion);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd($"LocalCam/{normalizedCurrent.ToString(3)}");

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("GitHub 上还没有可用的正式版本。请稍后再试。");
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var tagName = root.GetProperty("tag_name").GetString()
            ?? throw new InvalidOperationException("GitHub Release 缺少版本标签。");
        var latestVersion = ParseTag(tagName);
        var releasePage = ParseAbsoluteUri(root, "html_url", "GitHub Release 页面地址无效。");

        Uri? installerDownload = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(
                        asset.GetProperty("name").GetString(),
                        InstallerAssetName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                installerDownload = ParseAbsoluteUri(
                    asset,
                    "browser_download_url",
                    "GitHub 安装包下载地址无效。");
                break;
            }
        }

        if (installerDownload is null)
        {
            throw new InvalidOperationException($"最新 Release 中没有 {InstallerAssetName}。");
        }

        return new UpdateCheckResult(
            normalizedCurrent,
            new GitHubRelease(latestVersion, tagName, releasePage, installerDownload));
    }

    public static Version ParseTag(string tagName)
    {
        var value = tagName.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var suffix = value.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            value = value[..suffix];
        }

        if (!Version.TryParse(value, out var parsed) || parsed.Major < 0 || parsed.Minor < 0)
        {
            throw new InvalidOperationException($"无法识别 GitHub Release 版本标签：{tagName}");
        }

        return Normalize(parsed);
    }

    public static Version Normalize(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(version.Build, 0),
        Math.Max(version.Revision, 0));

    private static Uri ParseAbsoluteUri(JsonElement element, string propertyName, string errorMessage)
    {
        var value = element.GetProperty(propertyName).GetString();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(errorMessage);
        }

        return uri;
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();
}
