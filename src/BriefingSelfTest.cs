using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace XunXun;

static class BriefingSelfTest
{
    internal const string Weather = """
        {"daily":{"time":["2026-11-28"],"temperature_2m_min":[9],"temperature_2m_max":[16],"weather_code":[61]},
         "hourly":{"time":["2026-11-28T06:00","2026-11-28T07:00","2026-11-28T08:00","2026-11-28T10:00",
            "2026-11-28T11:00"],"precipitation_probability":[98,62,null,20,90]}}
        """;
    internal const string Repositories = """
        {"incomplete_results":false,"items":[
            {"full_name":"one/agent","stargazers_count":9,"description":"An agent","html_url":"https://github.com/one/agent"},
            {"full_name":"two/agent","stargazers_count":16,"html_url":"https://github.com/two/agent"},
            {"full_name":"two/agent","stargazers_count":15,"html_url":"https://github.com/two/agent"},
            {"full_name":"three/agent","stargazers_count":4,"html_url":"https://github.com/three/agent"},
            {"full_name":"four/agent","stargazers_count":2,"html_url":"https://github.com/four/agent"},
            {"full_name":"invalid/agent","stargazers_count":99,"html_url":"https://github.com.example.org/invalid/agent"}]}
        """;
    internal const string Cities = """
        {"results":[
            {"name":"上海","admin1":"上海","country":"中国","latitude":31.23,"longitude":121.47,"timezone":"Asia/Shanghai"},
            {"name":"同名","admin1":"甲","latitude":21,"longitude":110},
            {"name":"同名","admin1":"乙","latitude":22,"longitude":111},
            {"name":"无效","latitude":100,"longitude":111}]}
        """;

    internal static string Release(string version, bool prerelease = false) => $$"""
        {"draft":false,"prerelease":{{(prerelease ? "true" : "false")}},"tag_name":"{{version}}",
         "html_url":"https://github.com/openai/codex/releases/tag/{{version}}","published_at":"2026-11-26T19:00:00Z"}
        """;

    internal static string Feed(DateTimeOffset now)
    {
        string recent = now.UtcDateTime.ToString("r", CultureInfo.InvariantCulture);
        string old = now.AddDays(-10).UtcDateTime.ToString("r", CultureInfo.InvariantCulture);
        return $$"""
        <rss><channel>
          <item><title>Agent 定位</title><pubDate>{{recent}}</pubDate><link>https://openai.com/index/agent</link>
            <description><![CDATA[<p>研究 &amp; 实验</p>]]></description></item>
          <item><title>Agent 定位重复</title><pubDate>{{recent}}</pubDate><link>https://openai.com/index/agent?utm=duplicate</link></item>
          <item><title>Agent 工具</title><pubDate>{{recent}}</pubDate><link>https://openai.com/index/tool</link></item>
          <item><title>Agent 工作流</title><pubDate>{{recent}}</pubDate><link>https://openai.com/index/workflow</link></item>
          <item><title>Agent 更新</title><pubDate>{{recent}}</pubDate><link>https://openai.com/index/other</link></item>
          <item><title>旧文</title><pubDate>{{old}}</pubDate><link>https://openai.com/index/old</link></item>
          <item><title>缺日期</title><link>https://openai.com/index/undated</link></item>
          <item><title>地址错误</title><pubDate>{{recent}}</pubDate><link>http://openai.com/index/bad</link></item>
        </channel></rss>
        """;
    }

    public static async Task Check(string directory)
    {
        var now = new DateTimeOffset(DateTime.Today.AddHours(9));
        var settingsPath = Path.Combine(directory, "briefing-options-fixture.json");
        File.WriteAllText(settingsPath, "{");
        Require(LocalState.Read<BriefingOptions>(settingsPath) is null, "B01 damaged options did not fall back");
        File.WriteAllText(settingsPath, """{"Time":"25:99","Topic":"无效","Keywords":null}""");
        var defaults = LocalState.Read<BriefingOptions>(settingsPath)!.Normalize();
        Require(!defaults.Enabled && !defaults.Recap && defaults.Time == "08:30" && defaults.RecapTime == "18:00"
            && defaults.Topic == "ai-agent" && defaults.Keywords == "", "B01 partial options recovered incorrectly");

        var cities = BriefingClient.ParseCities(Cities);
        Require(cities.Length == 3 && cities[0].TimeZone == "Asia/Shanghai"
            && cities[1].Label.Contains('甲') && cities[2].Label.Contains('乙'),
            "B02 duplicate city names or invalid coordinates were not handled");
        var weather = BriefingClient.ParseWeather(Weather, cities[0]).Single();
        Require(weather.Rain && weather.Detail.Contains("62%") && weather.Detail.Contains("2026-11-28")
            && weather.Detail.Contains("9–16"), "B02 invalid morning weather result");
        var missing = BriefingClient.ParseWeather(Weather.Replace("[98,62,null,20,90]", "[98,null,null,null,90]"), cities[0]).Single();
        Require(!missing.Rain && missing.Detail.Contains("暂缺"), "B02 missing hourly probabilities were treated as real values");

        var repos = BriefingClient.ParseRepos(Repositories);
        Require(repos.Select(row => row.Title).SequenceEqual(["two/agent", "one/agent", "three/agent"])
            && BriefingClient.ParseRepos("""{"incomplete_results":false,"items":[]}""").Length == 0,
            "B04 ranking, cap, or empty search incorrect");
        Throws<InvalidDataException>(() => BriefingClient.ParseRepos(Repositories.Replace("false", "true")), "B04 incomplete search accepted");

        var rss = BriefingClient.ParseFeed(Feed(now), BriefingSource.OpenAI, now);
        string atom = $$"""
            <feed xmlns="http://www.w3.org/2005/Atom"><entry><title>Atom 文章</title>
              <link rel="alternate" href="/blog/atom-test"/><updated>{{now:O}}</updated>
            </entry></feed>
            """;
        Require(rss.Length == 4 && rss[0].Detail == "研究 & 实验"
            && BriefingClient.ParseFeed(atom, BriefingSource.DeepMind, now).Single().Url == "https://deepmind.google/blog/atom-test",
            "B05 RSS or Atom parsing, link deduplication, or date validation failed");
        var articles = new BriefingSnapshot(BriefingSource.OpenAI, "OpenAI", now, rss);
        Require(BriefingSchedule.News([articles], "Agent，模型", now).Length == 3
            && BriefingSchedule.News([articles], " unrelated ", now).Length == 0, "B05 keyword matching or item cap failed");
        Throws<XmlException>(() => BriefingClient.ParseFeed(
            """<!DOCTYPE rss [<!ENTITY x SYSTEM "file:///blocked">]><rss><channel>&x;</channel></rss>""",
            BriefingSource.OpenAI, now), "B05 external XML entity accepted");

        var configured = defaults with { City = cities[0], Weather = true, Github = true, Releases = true, News = false };
        var release = BriefingClient.ParseRelease(Release("v1"), BriefingSource.Codex);
        Require(release.Length == 1 && BriefingClient.ParseRelease(Release("v2-rc", true), BriefingSource.Codex).Length == 0
            && BriefingClient.ParseRelease(Release("v2").Replace("\"draft\":false", "\"draft\":true"), BriefingSource.Codex).Length == 0
            && BriefingClient.ParseRelease(Release("v2").Replace("github.com", "untrusted.example"), BriefingSource.Codex).Length == 0,
            "B03 prerelease, draft or untrusted link accepted");
        var releaseOnly = configured with { Weather = false, Github = false };
        BriefingState state = new(Cache: [Snapshot(releaseOnly, BriefingSource.Codex, now, release)]);
        var first = BriefingSchedule.Build(releaseOnly, state, now);
        Require(first.Copy == "" && first.Seen.Length == 1, "B03 first release raised an update");
        state = state with { Seen = first.Seen };
        Require(BriefingSchedule.Build(releaseOnly, state, now).Copy == "", "B03 unchanged release continued to notify");
        var newRelease = Snapshot(releaseOnly, BriefingSource.Codex, now, BriefingClient.ParseRelease(Release("v2"), BriefingSource.Codex));
        state = state.WithResults([new(BriefingSource.Codex, newRelease.Key, newRelease)]);
        Require(BriefingSchedule.Build(releaseOnly, state, now).Copy.Contains("1 个工具"), "B03 changed stable release was omitted");

        var cachedWeather = Snapshot(configured, BriefingSource.Weather, now, [weather]);
        var cachedRepos = Snapshot(configured, BriefingSource.Github, now, repos);
        state = state.WithResults([new(BriefingSource.Weather, cachedWeather.Key, cachedWeather),
            new(BriefingSource.Github, cachedRepos.Key, cachedRepos)]);
        Require(state.Find(configured with { City = cities[1] }, BriefingSource.Weather) is null
            && state.Find(configured with { Topic = "local-llm" }, BriefingSource.Github) is null,
            "B07 a changed setting reused a different location or topic");

        await CheckTransport(configured, state, now);
        await CheckWeatherFallback(configured, now);
        Require(!BriefingSchedule.IsDue(true, "08:30", null, now.AddMinutes(-31))
            && BriefingSchedule.IsDue(true, "08:30", null, now)
            && !BriefingSchedule.IsDue(true, "08:30", BriefingSchedule.Day(now), now.AddHours(1))
            && BriefingSchedule.IsDue(true, "08:30", BriefingSchedule.Day(now), now.AddDays(1)),
            "B08 cutoff, once per day or day rollover failed");
        var note = BriefingSchedule.Build(configured, state, now);
        string statePath = Path.Combine(directory, "briefing-state-fixture.json");
        LocalState.Write(statePath, state with { ShownDay = BriefingSchedule.Day(now), Seen = note.Seen });
        var reopened = LocalState.Read<BriefingState>(statePath)!;
        Require(!BriefingSchedule.IsDue(true, "08:30", reopened.ShownDay, now)
            && !BriefingSchedule.Build(configured, reopened, now).Copy.Contains("新项目"),
            "B08 shown day or content deduplication did not survive restart");

        var done = new Notice("Codex CLI", "done", Mood.Done, false, "test", 0, 0, 0);
        Require(TaskRecap.From([new(done, now), new(done, now.AddDays(-1)),
            new(done with { Mood = Mood.Approval }, now.AddDays(-1)),
            new(done with { Mood = Mood.Question }, now)], now) == new TaskRecap(1, 1, 1),
            "B09 recap did not use the local day or current pending requests");
        Require(!BriefingClient.CanOpen(BriefingSource.Github, "https://github.com@other.example/a/b")
            && !BriefingClient.CanOpen(BriefingSource.Github, "file:///C:/temp")
            && !BriefingClient.CanOpen(BriefingSource.OpenAI, "https://openai.com:444/index/test"),
            "B10 unsafe outbound link accepted");
    }

    private static async Task CheckWeatherFallback(BriefingOptions options, DateTimeOffset now)
    {
        using var http = new HttpClient(new FixtureHandler((request, _) => Task.FromResult(
            request.RequestUri!.Query.Contains("&hourly=", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Body("""{"daily":{"time":["2026-11-28"],"temperature_2m_min":[9],"temperature_2m_max":[16]}}"""))));
        var client = new BriefingClient(http);
        var fallback = await client.Fetch(BriefingSource.Weather, options, now, CancellationToken.None);
        Require(fallback.Snapshot?.Items.Single().Detail.Contains("上午降水概率暂缺") == true,
            "B06 unavailable hourly forecast did not fall back to labeled daily values");
    }

    private static async Task CheckTransport(BriefingOptions options, BriefingState state, DateTimeOffset now)
    {
        using var http = new HttpClient(new FixtureHandler(async (request, token) =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.open-meteo.com") return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (uri.AbsolutePath == "/search/repositories")
            {
                Require(Uri.UnescapeDataString(uri.Query).Contains("created:>=", StringComparison.Ordinal)
                    && uri.Query.Contains("sort=stars", StringComparison.Ordinal), "B04 search did not set the ranking scope");
                return Body(Repositories);
            }
            if (uri.AbsolutePath.Contains("codex", StringComparison.Ordinal)) return Body("{broken}");
            if (uri.Host == "huggingface.co") return Body(new string('A', 2 * 1024 * 1024 + 1));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("A pending request should have been canceled.");
        }));
        var transport = new BriefingClient(http, TimeSpan.FromMilliseconds(40));
        var result = await Task.WhenAll(new[] { BriefingSource.Weather, BriefingSource.Github, BriefingSource.Codex,
            BriefingSource.Claude, BriefingSource.HuggingFace }.Select(source => transport.Fetch(source, options, now, CancellationToken.None)));
        var updated = state.WithResults(result);
        Require(result.Count(row => row.Snapshot is not null) == 1 && result.Any(row => row.Error == "查询超时")
            && updated.Failure(options, BriefingSource.Weather)?.Message == "来源暂时限制访问"
            && updated.Find(options, BriefingSource.Weather) == state.Find(options, BriefingSource.Weather)
            && updated.Find(options, BriefingSource.Claude) is null && updated.Failure(options, BriefingSource.Claude) is not null
            && updated.Failure(options, BriefingSource.Codex) is not null
            && !BriefingSchedule.Build(options, updated, now).Copy.Contains("预报"),
            "B06 limited, timed-out, bad data and successful sources did not degrade independently");
        Require(updated.Failure(options, BriefingSource.HuggingFace) is not null, "B10 oversized response accepted");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        try
        {
            await transport.Fetch(BriefingSource.Claude, options, now, cancel.Token);
            throw new Exception("B10 canceled source returned new data");
        }
        catch (OperationCanceledException) { }
    }

    private static BriefingSnapshot Snapshot(BriefingOptions options, BriefingSource source, DateTimeOffset now,
        BriefingItem[] items) => new(source, options.Key(source), now, items);

    private static HttpResponseMessage Body(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
