using System.Text.Json;
using System.Text.Json.Serialization;

namespace WslTray;

public sealed class Config
{
    public int PollSeconds { get; set; } = 3;
    /// docker stats costs ~1s per call, so it runs on its own slower cadence.
    public int DockerPollSeconds { get; set; } = 10;

    /// Parent folder for newly created distros; each one gets its own subfolder.
    /// Empty means "ask WSL for its default" (under %LOCALAPPDATA%).
    public string DistroPath { get; set; } = "";

    [JsonIgnore] public string Path { get; private set; } = "";
    [JsonIgnore] public string? LoadError { get; private set; }
    [JsonIgnore] public bool Quarantined { get; private set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// Portable first: a config.json next to the .exe wins, so the tool can live on a
    /// USB stick. Otherwise %APPDATA%\wsl-tray\config.json.
    public static string ResolvePath()
    {
        var portable = System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");
        if (File.Exists(portable)) return portable;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(appData, "wsl-tray", "config.json");
    }

    public static Config Load()
    {
        var path = ResolvePath();
        Config cfg;
        try
        {
            cfg = File.Exists(path)
                ? JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new Config()
                : new Config();
        }
        catch (JsonException ex)
        {
            // Unparsable content, and only that, gets moved aside: the file is no use as it
            // is, and keeping it lets the user see what broke.
            try { File.Move(path, path + ".broken", overwrite: true); } catch { }
            cfg = new Config();
            cfg.LoadError = ex.Message + "\n\nIt was kept as config.json.broken and defaults are in use.";
        }
        catch (Exception ex)
        {
            // Anything else (a lock held by a backup tool, a momentary IO error) says nothing
            // about the content, so the file stays exactly where it is.
            cfg = new Config();
            cfg.LoadError = ex.Message + "\n\nThe file was left untouched; defaults are in use for this session.";
        }

        cfg.Path = path;
        // Clamped at both ends: the file is hand-editable (portable mode invites it), and a
        // value past Task.Delay's ceiling throws out of the polling loop, which then dies
        // for good with nothing on screen to say why.
        cfg.PollSeconds = Math.Clamp(cfg.PollSeconds, 1, 3600);
        cfg.DockerPollSeconds = Math.Clamp(cfg.DockerPollSeconds, cfg.PollSeconds, 3600);
        return cfg;
    }

    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // The load failed without quarantining the file, so what is on disk is the user's
        // real config and this object is a set of defaults. Keep a copy before overwriting
        // it, or a locked file at startup silently costs them every setting.
        if (LoadError != null && File.Exists(Path))
        {
            try { File.Copy(Path, Path + ".bak", overwrite: true); } catch { }
            LoadError = null;
        }

        // Written beside the target and moved into place: a half-written config.json is
        // unparsable, and an unparsable one next to the .exe silently ends portable mode.
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, Path, overwrite: true);
    }
}
