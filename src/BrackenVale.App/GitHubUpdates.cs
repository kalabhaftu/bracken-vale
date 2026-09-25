using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using BrackenVale.Core;

namespace BrackenVale.App;

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string Tag,
    [property: JsonPropertyName("html_url")] string Url,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease);

internal static class GitHubUpdates
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BrackenVale", typeof(GitHubUpdates).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static async Task<GitHubRelease?> GetLatestAsync(bool includePrerelease, CancellationToken cancellationToken = default)
    {
        var releases = await Client.GetFromJsonAsync<GitHubRelease[]>("https://api.github.com/repos/kalabhaftu/bracken-vale/releases?per_page=10", cancellationToken).ConfigureAwait(false);
        return releases?
            .Where(release => !release.Draft && (includePrerelease || !release.Prerelease))
            .Select(release => (Release: release, Valid: ReleaseVersion.TryParse(release.Tag, out var version), Version: version))
            .Where(item => item.Valid)
            .OrderByDescending(item => item.Version)
            .Select(item => item.Release)
            .FirstOrDefault();
    }
}
