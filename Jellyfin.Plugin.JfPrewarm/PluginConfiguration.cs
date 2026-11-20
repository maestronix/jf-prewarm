using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JfPrewarm;

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
    /// Last successful preload time, keyed by target episode item ID.
    /// </summary>
    public Dictionary<string, DateTime> LastPreloadUtc { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public long PreloadAttempts { get; set; }

    public long SuccessfulPreloads { get; set; }

    public long FailedPreloads { get; set; }

    public long BytesRead { get; set; }

    public string LastResult { get; set; } = "No preload attempts yet.";

    public DateTime? LastAttemptUtc { get; set; }
}
