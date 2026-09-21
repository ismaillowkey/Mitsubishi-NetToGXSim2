using System;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NetToGXSim2.Wpf.Services
{
    public class UpdateCheckResult
    {
        public bool Success { get; set; }
        public bool HasUpdate { get; set; }
        public string LatestVersion { get; set; } = string.Empty;
        public string ReleaseUrl { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public static class UpdateCheckerService
    {
        public const string CurrentVersion = "0.5.2";
        public const string ReleasesPageUrl = "https://github.com/ismaillowkey/Mitsubishi-NetToGXSim2/releases";
        public const string LatestReleaseApiUrl = "https://api.github.com/repos/ismaillowkey/Mitsubishi-NetToGXSim2/releases/latest";

        public static async Task<UpdateCheckResult> CheckForUpdatesAsync()
        {
            var result = new UpdateCheckResult
            {
                ReleaseUrl = ReleasesPageUrl
            };

            try
            {
                // Ensure modern TLS security protocols on .NET Framework
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "GX2Bridge-AutoUpdater");
                    client.Headers.Add("Accept", "application/vnd.github.v3+json");

                    string json = await client.DownloadStringTaskAsync(LatestReleaseApiUrl);

                    // Parse tag_name (e.g. "v0.3.2" or "0.3.2")
                    var tagMatch = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                    if (!tagMatch.Success)
                    {
                        result.Success = false;
                        result.ErrorMessage = "Unable to parse release tag from GitHub.";
                        return result;
                    }

                    string tagName = tagMatch.Groups[1].Value.Trim();
                    result.LatestVersion = tagName;

                    // Parse html_url if present
                    var urlMatch = Regex.Match(json, "\"html_url\"\\s*:\\s*\"([^\"]+)\"");
                    if (urlMatch.Success)
                    {
                        result.ReleaseUrl = urlMatch.Groups[1].Value.Trim();
                    }

                    // Compare versions
                    string cleanLatest = tagName.TrimStart('v', 'V');
                    string cleanCurrent = CurrentVersion.TrimStart('v', 'V');

                    if (Version.TryParse(cleanLatest, out var vLatest) && Version.TryParse(cleanCurrent, out var vCurrent))
                    {
                        result.HasUpdate = vLatest > vCurrent;
                    }
                    else
                    {
                        // Fallback string comparison
                        result.HasUpdate = !string.Equals(cleanLatest, cleanCurrent, StringComparison.OrdinalIgnoreCase);
                    }

                    result.Success = true;
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }
    }
}
