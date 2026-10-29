// Modified for the dengjie86 bilingual subtitle edition; see CHANGELOG.md.
using System.Net.Http;
using System.Text.Json;

namespace LiveCaptionsTranslator.utils
{
    public static class UpdateUtil
    {
        public const string GitHubRepoUrl = "https://github.com/dengjie86/LiveCaptions-Translator";
        public const string GitHubReleasesUrl = GitHubRepoUrl + "/releases";
        public const string GitHubLatestReleaseApi = "https://api.github.com/repos/dengjie86/LiveCaptions-Translator/releases/latest";

        public static async Task<string> GetLatestVersion()
        {
            string apiUrl = GitHubLatestReleaseApi;

            using var client = new HttpClient()
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveCaptions-Translator");
            var response = await client.GetStringAsync(apiUrl);
            using var doc = JsonDocument.Parse(response);
            var latestVersionRaw = doc.RootElement.GetProperty("tag_name").GetString();
            var latestVersion = string.IsNullOrEmpty(latestVersionRaw)
                ? String.Empty
                : RegexPatterns.VersionNumber().Replace(latestVersionRaw, "");
            return latestVersion;
        }


    }
}
