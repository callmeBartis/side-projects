using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClaudeUsageWidget;

public sealed record Limit(double Percent, DateTimeOffset? ResetsAt);
public sealed record Usage(Limit Session, Limit Weekly);

public sealed class UsageException(string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public static class UsageApi
{
    // Undocumented endpoint behind Claude Code's /usage. It can change without notice.
    private const string Url = "https://api.anthropic.com/api/oauth/usage";
    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string CredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public static async Task<Usage> FetchAsync()
    {
        // Read on every poll: Claude Code refreshes the token about every 8 hours and we
        // never refresh it ourselves, since that rotates the refresh token under Claude Code.
        string token;
        try
        {
            var creds = JsonNode.Parse(await File.ReadAllTextAsync(CredentialsPath));
            token = creds?["claudeAiOauth"]?["accessToken"]?.GetValue<string>() ?? "";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new UsageException("Kein Claude-Login gefunden");
        }
        if (token == "") throw new UsageException("Kein Claude-Login gefunden");

        using var req = new HttpRequestMessage(HttpMethod.Get, Url);
        req.Headers.Authorization = new("Bearer", token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        using var res = await Http.SendAsync(req);

        if (res.StatusCode == HttpStatusCode.Unauthorized)
            throw new UsageException("Token abgelaufen, Claude Code kurz benutzen");
        if (res.StatusCode == HttpStatusCode.TooManyRequests)
            throw new UsageException("Rate limit", res.Headers.RetryAfter?.Delta ?? TimeSpan.Zero);
        if (!res.IsSuccessStatusCode)
            throw new UsageException($"API-Fehler {(int)res.StatusCode}");

        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        return new Usage(Parse(json?["five_hour"]), Parse(json?["seven_day"]));
    }

    private static Limit Parse(JsonNode? n) => new(
        n?["utilization"]?.GetValue<double>() ?? 0,
        n?["resets_at"]?.GetValue<string>() is { } s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture) : null);

    private static readonly CultureInfo De = new("de-DE");

    /// "13:50" when the reset is today, "Mi 04:00" otherwise.
    public static string FormatReset(DateTimeOffset? at)
    {
        if (at is not { } t) return "-";
        // The API returns times like 18:49:59.9 for an 18:50 reset.
        var local = t.ToLocalTime().AddSeconds(30);
        return local.Date == DateTime.Today ? local.ToString("HH:mm", De) : local.ToString("ddd HH:mm", De);
    }
}

public static class Alerts
{
    /// Returns the highest threshold newly crossed (or null) and the new "already notified"
    /// level. A percentage below the last alert means the window has reset, so it rearms.
    public static (int? Fire, int Last) Check(double percent, int[] thresholds, int last)
    {
        var crossed = thresholds.Where(t => t <= percent).DefaultIfEmpty(0).Max();
        if (percent < last) return (null, crossed);
        return crossed > last ? (crossed, crossed) : (null, last);
    }
}

public static class Ntfy
{
    private static readonly JsonSerializerOptions Opts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // JSON publish instead of headers: titles with umlauts are not valid in HTTP headers.
    public static async Task SendAsync(string topic, string title, string message, int priority = 3, string? tag = null)
    {
        var body = new { topic, title, message, priority, tags = tag == null ? null : new[] { tag } };
        using var res = await UsageApi.Http.PostAsJsonAsync("https://ntfy.sh/", body, Opts);
        res.EnsureSuccessStatusCode();
    }
}

public sealed class Config
{
    public bool NtfyEnabled { get; set; }
    public string NtfyTopic { get; set; } = "";
    public int[] SessionAlerts { get; set; } = [50, 80, 95];
    public int[] WeeklyAlerts { get; set; } = [30, 40, 50, 60, 70, 80, 90, 100];
    public int PollSeconds { get; set; } = 300;
    public int StatusMinutes { get; set; } = 10;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "claude-usage-widget");
    [JsonIgnore] public string StatusTopic => NtfyTopic + "-status";

    public static Config Load()
    {
        var cfg = Json.Load<Config>("config.json");
        if (cfg != null) return cfg;
        cfg = new Config();
        cfg.Save(); // so "Einstellungen öffnen" has a file to open
        return cfg;
    }

    public void Save() => Json.Save("config.json", this);

    // ntfy.sh topics are public; an unguessable name is the only protection on the free tier.
    public static string NewTopic() =>
        "claude-usage-" + RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 20);
}

/// Kept apart from config.json so the app never overwrites the user's edits.
public sealed class State
{
    public int SessionNotified { get; set; }
    public int WeeklyNotified { get; set; }
    public Usage? Last { get; set; }
    public DateTime LastFetched { get; set; }
    public static State Load() => Json.Load<State>("state.json") ?? new State();
    public void Save() => Json.Save("state.json", this);
}

internal static class Json
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Opts), Opts)!;

    public static T? Load<T>(string name)
    {
        var path = Path.Combine(Config.Dir, name);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Opts) : default;
    }

    public static void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(Config.Dir);
        File.WriteAllText(Path.Combine(Config.Dir, name), JsonSerializer.Serialize(value, Opts));
    }
}
