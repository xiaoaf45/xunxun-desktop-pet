using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace XunXun;

enum BriefingSource { Weather, Claude, Codex, Github, OpenAI, DeepMind, HuggingFace }

sealed record BriefingCity(string Label, double Latitude, double Longitude, string? TimeZone = null)
{
    public bool Valid => !string.IsNullOrWhiteSpace(Label) && Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;
}

sealed record BriefingOptions(bool Enabled = false, string Time = "08:30", bool Weather = true,
    BriefingCity? City = null, bool Releases = true, bool Github = true, string Topic = "ai-agent",
    bool News = true, string Keywords = "", bool Recap = false, string RecapTime = "18:00")
{
    public static bool ValidTime(string? text) => TryTime(text, out _);

    public static bool TryTime(string? text, out TimeOnly time) => TimeOnly.TryParseExact(text, "HH:mm",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static bool ValidTopic(string? text) => text is not null && Regex.IsMatch(text, @"\A[a-z0-9][a-z0-9-]{0,34}\z");

    public BriefingOptions Normalize() => this with
    {
        Time = ValidTime(Time) ? Time : "08:30",
        RecapTime = ValidTime(RecapTime) ? RecapTime : "18:00",
        Topic = ValidTopic(Topic) ? Topic : "ai-agent",
        City = City is { Valid: true } ? City : null,
        Keywords = BriefingClient.Text(Keywords, 160)
    };

    public IEnumerable<BriefingSource> Sources()
    {
        if (Weather && City is { Valid: true }) yield return BriefingSource.Weather;
        if (Releases)
        {
            yield return BriefingSource.Claude;
            yield return BriefingSource.Codex;
        }
        if (Github) yield return BriefingSource.Github;
        if (News)
        {
            yield return BriefingSource.OpenAI;
            yield return BriefingSource.DeepMind;
            yield return BriefingSource.HuggingFace;
        }
    }

    public string Key(BriefingSource source) => source switch
    {
        BriefingSource.Weather when City is { } city => FormattableString.Invariant(
            $"weather:{city.Latitude:R},{city.Longitude:R}:{city.TimeZone}:{city.Label}"),
        BriefingSource.Github => $"github:{Topic}",
        _ => source.ToString()
    };
}

sealed record BriefingItem(string Id, string Title, string Detail, string Url, DateTimeOffset? Published = null,
    bool Rain = false);
sealed record BriefingSnapshot(BriefingSource Source, string Key, DateTimeOffset FetchedAt, BriefingItem[] Items);
sealed record BriefingResult(BriefingSource Source, string Key, BriefingSnapshot? Snapshot, string? Error = null);

sealed class BriefingClient(HttpClient? client = null, TimeSpan? requestTimeout = null)
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private static readonly HttpClient Shared = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly HttpClient http = client ?? Shared;
    private static readonly Regex Markup = new("<[^>]*>", RegexOptions.NonBacktracking);

    public static string SourceName(BriefingSource source) => source switch
    {
        BriefingSource.Weather => "Open-Meteo",
        BriefingSource.Claude => "Claude Code",
        BriefingSource.Codex => "Codex CLI",
        BriefingSource.Github => "GitHub",
        BriefingSource.OpenAI => "OpenAI",
        BriefingSource.DeepMind => "Google DeepMind",
        BriefingSource.HuggingFace => "Hugging Face",
        _ => ""
    };

    public async Task<BriefingCity[]> FindCities(string query, CancellationToken token)
    {
        var body = await Get($"https://geocoding-api.open-meteo.com/v1/search?count=8&language=zh&name={Uri.EscapeDataString(query)}", token)
            .ConfigureAwait(false);
        return ParseCities(body);
    }

    public async Task<BriefingResult> Fetch(BriefingSource source, BriefingOptions options,
        DateTimeOffset now, CancellationToken token)
    {
        string key = options.Key(source);
        try
        {
            string since = now.UtcDateTime.Date.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string endpoint = source switch
            {
                BriefingSource.Weather when options.City is { } city => FormattableString.Invariant(
                    $"https://api.open-meteo.com/v1/forecast?latitude={city.Latitude:R}&longitude={city.Longitude:R}&timezone={Uri.EscapeDataString(city.TimeZone ?? "auto")}&forecast_days=1&daily=temperature_2m_max%2Ctemperature_2m_min%2Cweather_code"),
                BriefingSource.Claude => "https://api.github.com/repos/anthropics/claude-code/releases/latest",
                BriefingSource.Codex => "https://api.github.com/repos/openai/codex/releases/latest",
                BriefingSource.Github => $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(
                    $"topic:{options.Topic} created:>={since} fork:false archived:false")}&sort=stars&order=desc&per_page=8",
                _ => FeedUrl(source)
            };
            var body = source == BriefingSource.Weather
                ? await GetWeather(endpoint, token).ConfigureAwait(false)
                : await Get(endpoint, token).ConfigureAwait(false);
            var items = source switch
            {
                BriefingSource.Weather => ParseWeather(body, options.City!),
                BriefingSource.Claude or BriefingSource.Codex => ParseRelease(body, source),
                BriefingSource.Github => ParseRepos(body),
                _ => ParseFeed(body, source, now)
            };
            return new(source, key, new(source, key, now, items));
        }
        catch (Exception error) when (IsSourceError(error) && !token.IsCancellationRequested)
        {
            return new(source, key, null, ErrorMessage(error));
        }
    }

    private async Task<string> GetWeather(string endpoint, CancellationToken token)
    {
        try { return await Get(endpoint + "&hourly=precipitation_probability", token).ConfigureAwait(false); }
        catch (Exception error) when (!token.IsCancellationRequested && (error is OperationCanceledException
            || error is HttpRequestException { StatusCode: null or >= HttpStatusCode.InternalServerError }))
        {
            return await Get(endpoint, token).ConfigureAwait(false);
        }
    }

    private async Task<string> Get(string url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("XunXun-DesktopPet/1.0");
        if (request.RequestUri!.Host == "api.github.com")
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("Response too large.");
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var data = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (data.Length + count > MaxBytes) throw new InvalidDataException("Response too large.");
            data.Write(buffer, 0, count);
        }
        data.Position = 0;
        using var reader = new StreamReader(data);
        return await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
    }

    internal static bool IsSourceError(Exception error) => error is HttpRequestException or OperationCanceledException
        or IOException or InvalidDataException or JsonException or XmlException or FormatException
        or KeyNotFoundException or InvalidOperationException;

    internal static string ErrorMessage(Exception error) => error switch
    {
        OperationCanceledException => "查询超时",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } => "来源暂时限制访问",
        HttpRequestException => "暂时无法连接来源",
        _ => "暂时无法读取来源数据"
    };

    internal static BriefingCity[] ParseCities(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("results", out var rows)) return [];
        var cities = new List<BriefingCity>();
        foreach (var row in rows.EnumerateArray())
        {
            string name = Text(Field(row, "name"), 70);
            if (name.Length == 0 || !Number(row, "latitude", out var latitude) || !Number(row, "longitude", out var longitude)) continue;
            var parts = new[] { name, Text(Field(row, "admin1"), 55), Text(Field(row, "country"), 45) }
                .Where(part => part.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
            string zone = Text(Field(row, "timezone"), 70);
            var city = new BriefingCity(string.Join(" · ", parts), latitude, longitude, zone.Length > 0 ? zone : null);
            if (city.Valid) cities.Add(city);
        }
        return cities.Distinct().Take(8).ToArray();
    }

    internal static BriefingItem[] ParseWeather(string body, BriefingCity city)
    {
        using var doc = JsonDocument.Parse(body);
        var daily = doc.RootElement.GetProperty("daily");
        string? date = daily.GetProperty("time")[0].GetString();
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || !daily.GetProperty("temperature_2m_min")[0].TryGetDouble(out var low)
            || !daily.GetProperty("temperature_2m_max")[0].TryGetDouble(out var high)
            || !double.IsFinite(low) || !double.IsFinite(high) || low > high)
            throw new InvalidDataException("Missing daily forecast.");

        int? rain = null;
        if (doc.RootElement.TryGetProperty("hourly", out var hourly)
            && hourly.TryGetProperty("time", out var times)
            && hourly.TryGetProperty("precipitation_probability", out var chances))
        {
            for (int i = 0; i < Math.Min(times.GetArrayLength(), chances.GetArrayLength()); i++)
                if (times[i].GetString() is { Length: >= 16 } time
                    && time.StartsWith(date + "T", StringComparison.Ordinal)
                    && int.TryParse(time.AsSpan(11, 2), out int hour) && hour is >= 7 and <= 10
                    && chances[i].ValueKind == JsonValueKind.Number && chances[i].TryGetInt32(out int percent)
                    && percent is >= 0 and <= 100)
                    rain = Math.Max(rain ?? 0, percent);
        }

        int? code = daily.TryGetProperty("weather_code", out var codes) && codes.GetArrayLength() > 0
            && codes[0].ValueKind == JsonValueKind.Number && codes[0].TryGetInt32(out int value) ? value : null;
        string forecast = code switch
        {
            0 => "晴", >= 1 and <= 3 => "多云", 45 or 48 => "雾",
            >= 51 and <= 67 or >= 80 and <= 82 => "有雨",
            >= 71 and <= 77 or 85 or 86 => "降雪", >= 95 and <= 99 => "雷雨", _ => ""
        };
        string morning = rain is { } chance ? $"07:00–10:00 最高降水概率 {chance}%" : "上午降水概率暂缺";
        string detail = FormattableString.Invariant($"{date}  ·  {forecast}{(forecast.Length > 0 ? " " : "")}{low:0.#}–{high:0.#} °C  ·  {morning}");
        return [new($"weather/{date}", city.Label, detail, "https://open-meteo.com/", Rain: rain >= 50)];
    }

    internal static BriefingItem[] ParseRelease(string body, BriefingSource source)
    {
        using var doc = JsonDocument.Parse(body);
        var row = doc.RootElement;
        if (!row.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
            || !row.TryGetProperty("prerelease", out var pre) || pre.ValueKind != JsonValueKind.False) return [];
        string version = Text(Field(row, "tag_name"), 96);
        string url = Field(row, "html_url") ?? "";
        if (version.Length == 0 || !CanOpen(source, url)) return [];
        return [new($"release/{source}/{version}", $"{SourceName(source)} {version}", "最新正式版",
            url, ParseDate(Field(row, "published_at")))];
    }

    internal static BriefingItem[] ParseRepos(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.GetProperty("incomplete_results").ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Incomplete GitHub ranking.");
        var repositories = new List<(BriefingItem Item, int Stars)>();
        foreach (var row in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string url = Field(row, "html_url") ?? "";
            string name = Text(Field(row, "full_name"), 120);
            if (!CanOpen(BriefingSource.Github, url) || name.Length == 0
                || !row.GetProperty("stargazers_count").TryGetInt32(out int stars) || stars < 0) continue;
            string description = Text(Field(row, "description"), 150);
            string detail = $"★ {stars:N0}" + (description.Length > 0 ? $"  ·  {description}" : "");
            repositories.Add((new($"repo/{name.ToLowerInvariant()}", name, detail, url, ParseDate(Field(row, "created_at"))), stars));
        }
        return repositories.OrderByDescending(repo => repo.Stars).Select(repo => repo.Item)
            .DistinctBy(item => item.Id).Take(3).ToArray();
    }

    internal static BriefingItem[] ParseFeed(string body, BriefingSource source, DateTimeOffset now)
    {
        using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes
        });
        var doc = XDocument.Load(reader);
        if (doc.Root?.Name.LocalName is not ("rss" or "feed")) throw new XmlException("Unknown feed format.");
        var rows = doc.Root.Name.LocalName == "feed" ? doc.Root.Elements()
            : doc.Root.Elements().Where(el => el.Name.LocalName == "channel").SelectMany(el => el.Elements());
        var items = new List<BriefingItem>();
        foreach (var row in rows.Where(el => el.Name.LocalName is "item" or "entry"))
        {
            string title = Text(Element(row, "title"), 130);
            var href = row.Elements().FirstOrDefault(el => el.Name.LocalName == "link"
                && (string?)el.Attribute("rel") is null or "alternate");
            string? link = (string?)href?.Attribute("href") ?? href?.Value;
            var date = ParseDate(Element(row, "pubDate") ?? Element(row, "published") ?? Element(row, "updated"));
            if (title.Length == 0 || date is null || date < now.AddDays(-7) || date > now.AddMinutes(10)
                || !Uri.TryCreate(new Uri(FeedUrl(source)), link, out var uri) || !CanOpen(source, uri.AbsoluteUri)) continue;
            string description = Text(Markup.Replace(Element(row, "description") ?? Element(row, "summary") ?? "", " "), 170);
            string id = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            items.Add(new(id, title, description, uri.AbsoluteUri, date));
        }
        return items.OrderByDescending(item => item.Published).DistinctBy(item => item.Id).Take(50).ToArray();
    }

    public static bool CanOpen(BriefingSource source, string? link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return false;
        return source switch
        {
            BriefingSource.Weather => uri.Host == "open-meteo.com",
            BriefingSource.Claude => uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/anthropics/claude-code/releases/", StringComparison.OrdinalIgnoreCase),
            BriefingSource.Codex => uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/openai/codex/releases/", StringComparison.OrdinalIgnoreCase),
            BriefingSource.Github => uri.Host == "github.com" && Regex.IsMatch(uri.AbsolutePath, @"\A/[a-zA-Z0-9-]+/[a-zA-Z0-9_.-]+/?\z"),
            BriefingSource.OpenAI => uri.Host is "openai.com" or "www.openai.com",
            BriefingSource.DeepMind => uri.Host == "deepmind.google",
            BriefingSource.HuggingFace => uri.Host == "huggingface.co" && uri.AbsolutePath.StartsWith("/blog/", StringComparison.Ordinal),
            _ => false
        };
    }

    internal static bool IsNews(BriefingSource source) => source is BriefingSource.OpenAI or BriefingSource.DeepMind
        or BriefingSource.HuggingFace;

    private static string FeedUrl(BriefingSource source) => source switch
    {
        BriefingSource.OpenAI => "https://openai.com/news/rss.xml",
        BriefingSource.DeepMind => "https://deepmind.google/blog/rss.xml",
        BriefingSource.HuggingFace => "https://huggingface.co/blog/feed.xml",
        _ => throw new InvalidOperationException("Feed unavailable.")
    };

    private static string? Field(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Number(JsonElement row, string name, out double number)
    {
        number = 0;
        return row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number);
    }

    private static string? Element(XElement row, string name) =>
        row.Elements().FirstOrDefault(el => el.Name.LocalName == name)?.Value;

    private static DateTimeOffset? ParseDate(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    internal static string Text(string? value, int max)
    {
        string text = Regex.Replace(WebUtility.HtmlDecode(value ?? ""), @"[\s\p{Cc}]+", " ").Trim();
        if (text.Length <= max) return text;
        int end = max - 1;
        if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end] + "…";
    }
}
