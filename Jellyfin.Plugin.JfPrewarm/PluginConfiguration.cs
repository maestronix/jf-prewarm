using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JfPrewarm;

/// <summary>
/// One persisted cooldown entry. A list is used because Jellyfin stores plugin
/// configuration as XML and XmlSerializer cannot serialize Dictionary types.
/// </summary>
public sealed class PreloadCooldownEntry
{
    /// <summary>
    /// Legacy XML element name retained for backwards compatibility.
    /// The value is now the ID of any preloaded media item, not only an episode.
    /// </summary>
    public string EpisodeId { get; set; } = string.Empty;

    public DateTime LastPreloadUtc { get; set; }
}

/// <summary>
/// Plugin settings and persisted runtime state.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Playback percentage at which the next episode is queued.
    /// </summary>
    public int TriggerPercent { get; set; } = 90;

    /// <summary>
    /// Last successful preload times, keyed by target media item ID.
    /// The EpisodeId XML element name is retained for configuration compatibility.
/// </summary>
    public List<PreloadCooldownEntry> LastPreloads { get; set; } = [];

    public long PreloadAttempts { get; set; }

    public long SuccessfulPreloads { get; set; }

    public long FailedPreloads { get; set; }

    public long BytesRead { get; set; }

    public string LastResult { get; set; } = "No preload attempts yet.";

    public DateTime? LastAttemptUtc { get; set; }
}
