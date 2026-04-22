using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Linkout;

/// <summary>
/// 链接净化器：
/// - 从分享文本中提取 URL；
/// - 按平台去除常见追踪参数；
/// - 支持通用 tracking 参数清洗。
/// </summary>
internal sealed partial class LinkCleaner
{
    private const int MaxRedirectHops = 5;
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15)
    })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    // 提取文本中的 URL（先抓到候选，再做末尾标点修剪）。
    [GeneratedRegex(@"https?://[^\s\u4e00-\u9fff]+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 100)]
    private static partial Regex UrlRegex();

    private static readonly HashSet<string> KnownTrackingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "spm", "spm_id_from", "vd_source", "si", "share_source", "share_medium",
        "share_plat", "share_iid", "timestamp", "from", "source", "src", "trackid",
        "xsec_token", "xsec_source", "sessionid", "sec_user_id", "enter_from"
    };

    public ValueTask<string> CleanAsync(string input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return ValueTask.FromResult(input);
        }

        // 改为“行内替换”模式：保留原始文本，仅替换其中可清洗的 URL。
        var urls = UrlRegex().Matches(input)
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (urls.Count == 0)
        {
            return ValueTask.FromResult(input);
        }

        return new ValueTask<string>(CleanWithUrlsAsync(input, urls, cancellationToken));
    }

    private async Task<string> CleanWithUrlsAsync(
        string input,
        List<string> urls,
        CancellationToken cancellationToken)
    {
        var result = input;

        foreach (var rawUrl in urls)
        {
            var trimmedUrl = TrimUrlTailPunctuation(rawUrl);
            if (!Uri.TryCreate(trimmedUrl, UriKind.Absolute, out _))
            {
                continue;
            }

            var expandedUrl = await ResolveShortLinkIfNeededAsync(trimmedUrl, cancellationToken);
            var cleanedUrl = RemoveTrailingSlash(CleanUrlByRules(expandedUrl));
            if (string.IsNullOrWhiteSpace(cleanedUrl))
            {
                continue;
            }

            if (!string.Equals(trimmedUrl, cleanedUrl, StringComparison.Ordinal))
            {
                // 按需求使用 Replace：将原文中的 URL 原样片段替换为净化后的 URL。
                // 若原 URL 后带中文标点，rawUrl 形如 "https://.../?a=1，"，
                // 此时替换为 "https://...<cleaned>，" 以保留末尾标点与行文语义。
                if (!string.Equals(rawUrl, trimmedUrl, StringComparison.Ordinal))
                {
                    var suffix = rawUrl[trimmedUrl.Length..];
                    result = result.Replace(rawUrl, cleanedUrl + suffix, StringComparison.Ordinal);
                }
                else
                {
                    result = result.Replace(rawUrl, cleanedUrl, StringComparison.Ordinal);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 当 URL 命中短链接特征时，读取重定向响应头 Location 还原真实长链。
    /// 仅使用 ResponseHeadersRead，不下载页面正文。
    /// </summary>
    private static async Task<string> ResolveShortLinkIfNeededAsync(string url, CancellationToken cancellationToken)
    {
        if (!TryGetRedirectStartUrl(url, out var redirectStartUrl))
        {
            return url;
        }

        var currentUrl = redirectStartUrl;
        try
        {
            for (var hop = 0; hop < MaxRedirectHops; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                using var response = await HttpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (!IsRedirectStatusCode(response.StatusCode) || response.Headers.Location is null)
                {
                    return currentUrl;
                }

                var nextUri = response.Headers.Location;
                if (!nextUri.IsAbsoluteUri)
                {
                    nextUri = new Uri(new Uri(currentUrl), nextUri);
                }

                currentUrl = nextUri.ToString();
            }
        }
        catch
        {
            // 网络异常、超时等场景回退使用原始 URL，不影响主净化流程。
            return url;
        }

        return currentUrl;
    }

    /// <summary>
    /// 识别短链接入口：
    /// - b23.tv
    /// - m.tb.cn
    /// - v.douyin.com
    /// - google 搜索页中的 q=v.douyin.com/... 场景
    /// </summary>
    private static bool TryGetRedirectStartUrl(string url, out string redirectStartUrl)
    {
        redirectStartUrl = url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host == "b23.tv" || host.EndsWith(".b23.tv", StringComparison.Ordinal)
            || host == "m.tb.cn" || host.EndsWith(".m.tb.cn", StringComparison.Ordinal)
            || host == "v.douyin.com" || host.EndsWith(".v.douyin.com", StringComparison.Ordinal))
        {
            return true;
        }

        // 支持: https://www.google.com/search?q=v.douyin.com/xxxx
        if (host.Contains("google.com", StringComparison.Ordinal) && uri.AbsolutePath.Equals("/search", StringComparison.OrdinalIgnoreCase))
        {
            var query = ParseQuery(uri.Query);
            if (!query.TryGetValue("q", out var qValue) || string.IsNullOrWhiteSpace(qValue))
            {
                return false;
            }

            var decoded = qValue.Trim();
            if (decoded.StartsWith("v.douyin.com", StringComparison.OrdinalIgnoreCase))
            {
                decoded = $"https://{decoded}";
            }

            if (Uri.TryCreate(decoded, UriKind.Absolute, out var qUri))
            {
                var qHost = qUri.Host.ToLowerInvariant();
                if (qHost == "v.douyin.com" || qHost.EndsWith(".v.douyin.com", StringComparison.Ordinal))
                {
                    redirectStartUrl = qUri.ToString();
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static bool IsKnownPlatformUrl(string url)
    {
        var host = GetHost(url);
        return host.Contains("bilibili.com")
               || host.Contains("b23.tv")
               || host.Contains("xiaohongshu.com")
               || host.Contains("xhslink.com")
               || host.Contains("douyin.com")
               || host.Contains("iesdouyin.com")
               || host.Contains("taobao.com")
               || host.Contains("tmall.com");
    }

    private static string CleanUrlByRules(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var host = uri.Host.ToLowerInvariant();
        var query = ParseQuery(uri.Query);
        var keepQuery = new List<KeyValuePair<string, string>>();

        if (host.Contains("bilibili.com") || host.Contains("b23.tv"))
        {
            // Bilibili 重点保留分P参数 p，其余常见追踪参数丢弃。
            if (query.TryGetValue("p", out var pValue) && !string.IsNullOrWhiteSpace(pValue))
            {
                keepQuery.Add(new KeyValuePair<string, string>("p", pValue));
            }
        }
        else
        {
            // 其他平台及通用场景：仅保留非追踪参数。
            foreach (var pair in query)
            {
                if (!IsTrackingKey(pair.Key))
                {
                    keepQuery.Add(new KeyValuePair<string, string>(pair.Key, pair.Value));
                }
            }
        }

        var baseUrl = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
        return keepQuery.Count == 0
            ? baseUrl
            : $"{baseUrl}?{BuildQueryString(keepQuery)}";
    }

    private static string GetHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host.ToLowerInvariant()
            : string.Empty;
    }

    private static bool IsTrackingKey(string key)
    {
        if (KnownTrackingKeys.Contains(key))
        {
            return true;
        }

        return key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
               || key.StartsWith("spm_", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
        {
            return dict;
        }

        var trimmed = query.TrimStart('?');
        var pairs = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..idx]);
            var value = Uri.UnescapeDataString(pair[(idx + 1)..]);
            dict[key] = value;
        }

        return dict;
    }

    private static string BuildQueryString(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        return string.Join("&", pairs.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
    }

    private static string RemoveTrailingSlash(string url)
    {
        return url.EndsWith("/", StringComparison.Ordinal)
            ? url.TrimEnd('/')
            : url;
    }

    private static string TrimUrlTailPunctuation(string url)
    {
        return url.TrimEnd('。', '，', '！', '？', '.', ',', '!', '?', ';', ')', '）', ']', '"', '\'');
    }
}
