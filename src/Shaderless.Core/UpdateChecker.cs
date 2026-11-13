using System.Net.Http.Headers;
using System.Text.Json;

namespace Shaderless.Core;

public record UpdateCheckResult(
    string CurrentVersion,
    string LatestTag,
    string LatestVersion,
    string ReleaseUrl,
    bool IsPrerelease,
    DateTime? PublishedAtUtc
);

/// <summary>
/// Non-blocking GitHub Releases checker for https://github.com/cyberps96/Shaderless.
/// Supports both stable releases and beta pre-releases without any self-updater behavior.
/// </summary>
public static class UpdateChecker
{
    public const string ReleasesApiUrl = "https://api.github.com/repos/cyberps96/Shaderless/releases?per_page=8";
    public const string FallbackReleasesPageUrl = "https://github.com/cyberps96/Shaderless/releases";

    private static readonly HttpClient SharedHttpClient = CreateDefaultClient();

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Shaderless/{AppPaths.Version}");
        return client;
    }

    public static async Task<UpdateCheckResult?> CheckForUpdateAsync(
        string? currentVersion = null,
        string? dismissedTag = null,
        CancellationToken cancellationToken = default)
    {
        string cur = string.IsNullOrWhiteSpace(currentVersion) ? AppPaths.Version : currentVersion.Trim();
        if (string.Equals(cur, "dev", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using var response = await SharedHttpClient.GetAsync(ReleasesApiUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return EvaluateReleasesJson(json, cur, dismissedTag);
        }
        catch
        {
            // Offline, firewalled, DNS failure, or GitHub rate limit: silently ignore
            return null;
        }
    }

    public static UpdateCheckResult? EvaluateReleasesJson(
        string? json,
        string currentVersion,
        string? dismissedTag = null)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(currentVersion))
            return null;

        if (!TryParseSemVer(currentVersion, out var currentParsed))
            return null;

        bool allowPrereleases = !string.IsNullOrEmpty(currentParsed.Prerelease);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                return TryEvaluateCandidate(root, currentVersion, currentParsed, allowPrereleases, dismissedTag, out var single)
                    ? single
                    : null;
            }

            if (root.ValueKind != JsonValueKind.Array)
                return null;

            UpdateCheckResult? bestResult = null;
            ParsedSemVer? bestSemVer = null;

            foreach (var elem in root.EnumerateArray())
            {
                if (!TryEvaluateCandidate(elem, currentVersion, currentParsed, allowPrereleases, dismissedTag: null, out var candidate) ||
                    candidate == null)
                {
                    continue;
                }

                if (!TryParseSemVer(candidate.LatestVersion, out var candVer))
                    continue;

                if (bestSemVer == null || CompareParsedSemVer(candVer, bestSemVer.Value) > 0)
                {
                    bestSemVer = candVer;
                    bestResult = candidate;
                }
            }

            if (bestResult == null)
                return null;

            if (IsTagMatchingDismissed(bestResult.LatestTag, bestResult.LatestVersion, dismissedTag))
                return null;

            return bestResult;
        }
        catch
        {
            return null;
        }
    }

    public static int CompareSemVer(string left, string right)
    {
        bool okL = TryParseSemVer(left, out var l);
        bool okR = TryParseSemVer(right, out var r);
        if (!okL && !okR) return 0;
        if (!okL) return -1;
        if (!okR) return 1;
        return CompareParsedSemVer(l, r);
    }

    private static bool TryEvaluateCandidate(
        JsonElement elem,
        string currentVersion,
        ParsedSemVer currentParsed,
        bool allowPrereleases,
        string? dismissedTag,
        out UpdateCheckResult? result)
    {
        result = null;
        if (elem.ValueKind != JsonValueKind.Object)
            return false;

        if (elem.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True)
            return false;

        bool isPrerelease = elem.TryGetProperty("prerelease", out var preProp) && preProp.ValueKind == JsonValueKind.True;
        if (isPrerelease && !allowPrereleases)
            return false;

        if (!elem.TryGetProperty("tag_name", out var tagProp) || tagProp.ValueKind != JsonValueKind.String)
            return false;

        string? rawTag = tagProp.GetString()?.Trim();
        if (string.IsNullOrEmpty(rawTag) || !TryParseSemVer(rawTag, out var candParsed))
            return false;

        // Also treat SemVer pre-release tags as pre-release even if GitHub's prerelease checkbox wasn't ticked
        bool effectivePrerelease = isPrerelease || !string.IsNullOrEmpty(candParsed.Prerelease);
        if (effectivePrerelease && !allowPrereleases)
            return false;

        if (CompareParsedSemVer(candParsed, currentParsed) <= 0)
            return false;

        string htmlUrl = FallbackReleasesPageUrl;
        if (elem.TryGetProperty("html_url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
        {
            string? candidateUrl = urlProp.GetString()?.Trim();
            if (IsAllowedGitHubUrl(candidateUrl))
            {
                htmlUrl = candidateUrl!;
            }
            else
            {
                return false;
            }
        }

        DateTime? publishedAt = null;
        if (elem.TryGetProperty("published_at", out var pubProp) &&
            pubProp.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(pubProp.GetString(), out var dto))
        {
            publishedAt = dto.UtcDateTime;
        }

        string normalizedVersion = string.IsNullOrEmpty(candParsed.Prerelease)
            ? $"{candParsed.Major}.{candParsed.Minor}.{candParsed.Patch}"
            : $"{candParsed.Major}.{candParsed.Minor}.{candParsed.Patch}-{candParsed.Prerelease}";

        if (IsTagMatchingDismissed(rawTag, normalizedVersion, dismissedTag))
            return false;

        result = new UpdateCheckResult(
            CurrentVersion: currentVersion,
            LatestTag: rawTag,
            LatestVersion: normalizedVersion,
            ReleaseUrl: htmlUrl,
            IsPrerelease: effectivePrerelease,
            PublishedAtUtc: publishedAt);
        return true;
    }

    private static bool IsTagMatchingDismissed(string tag, string normalizedVersion, string? dismissedTag)
    {
        if (string.IsNullOrWhiteSpace(dismissedTag))
            return false;

        string cleanDismissed = NormalizeTagPrefix(dismissedTag.Trim());
        string cleanTag = NormalizeTagPrefix(tag);
        return string.Equals(cleanDismissed, cleanTag, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(cleanDismissed, normalizedVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedGitHubUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme == Uri.UriSchemeHttps &&
               (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase));
    }

    internal readonly record struct ParsedSemVer(int Major, int Minor, int Patch, string? Prerelease);

    internal static string NormalizeTagPrefix(string raw)
    {
        string s = raw.Trim();
        if (s.StartsWith("Shaderless-", StringComparison.OrdinalIgnoreCase))
            s = s["Shaderless-".Length..];
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
            s = s[1..];
        int plusIdx = s.IndexOf('+');
        if (plusIdx >= 0)
            s = s[..plusIdx];
        return s.Trim();
    }

    internal static bool TryParseSemVer(string? raw, out ParsedSemVer parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string s = NormalizeTagPrefix(raw);
        if (string.IsNullOrEmpty(s))
            return false;

        string? prerelease = null;
        int dashIdx = s.IndexOf('-');
        if (dashIdx >= 0)
        {
            prerelease = s[(dashIdx + 1)..].Trim();
            if (prerelease.Length == 0) prerelease = null;
            s = s[..dashIdx];
        }

        var parts = s.Split('.');
        if (parts.Length is < 2 or > 4)
            return false;

        if (!int.TryParse(parts[0], out int major) || major < 0)
            return false;
        if (!int.TryParse(parts[1], out int minor) || minor < 0)
            return false;
        int patch = 0;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out patch) || patch < 0))
            return false;

        parsed = new ParsedSemVer(major, minor, patch, prerelease);
        return true;
    }

    internal static int CompareParsedSemVer(ParsedSemVer a, ParsedSemVer b)
    {
        int cmp = a.Major.CompareTo(b.Major);
        if (cmp != 0) return cmp;
        cmp = a.Minor.CompareTo(b.Minor);
        if (cmp != 0) return cmp;
        cmp = a.Patch.CompareTo(b.Patch);
        if (cmp != 0) return cmp;

        bool aHasPre = !string.IsNullOrEmpty(a.Prerelease);
        bool bHasPre = !string.IsNullOrEmpty(b.Prerelease);
        if (!aHasPre && !bHasPre) return 0;
        if (!aHasPre) return 1;  // 0.3.0 (stable) > 0.3.0-beta
        if (!bHasPre) return -1;

        var aTokens = SplitPrereleaseTokens(a.Prerelease!);
        var bTokens = SplitPrereleaseTokens(b.Prerelease!);
        int len = Math.Min(aTokens.Count, bTokens.Count);
        for (int i = 0; i < len; i++)
        {
            bool aNum = long.TryParse(aTokens[i], out long aVal);
            bool bNum = long.TryParse(bTokens[i], out long bVal);
            if (aNum && bNum)
            {
                cmp = aVal.CompareTo(bVal);
                if (cmp != 0) return cmp;
            }
            else if (aNum != bNum)
            {
                // Numeric identifiers have lower precedence than non-numeric per SemVer 2.0
                return aNum ? -1 : 1;
            }
            else
            {
                cmp = string.Compare(aTokens[i], bTokens[i], StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
            }
        }

        return aTokens.Count.CompareTo(bTokens.Count);
    }

    private static List<string> SplitPrereleaseTokens(string pre)
    {
        // Split both on '.' (beta.1 vs beta.2) and alpha-digit boundaries (beta1 vs beta2 / rc1 vs beta2)
        var tokens = new List<string>();
        foreach (var seg in pre.Split(new[] { '.', '-' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int start = 0;
            for (int i = 1; i < seg.Length; i++)
            {
                if (char.IsDigit(seg[i]) != char.IsDigit(seg[i - 1]))
                {
                    tokens.Add(seg[start..i]);
                    start = i;
                }
            }
            tokens.Add(seg[start..]);
        }
        return tokens;
    }
}
