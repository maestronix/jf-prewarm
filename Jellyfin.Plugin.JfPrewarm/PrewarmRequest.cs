namespace Jellyfin.Plugin.JfPrewarm;

/// <summary>
/// Identifies the event that requested a preload.
/// </summary>
public enum PrewarmTriggerSource
{
    PlaybackProgress,
    ItemDetail,
    SessionStarted
}

/// <summary>
/// Small, immutable message passed from trigger handlers to the background queue.
/// UserId is reserved for user-specific candidate selection; it is never used
/// as part of the global cooldown key.
/// </summary>
public sealed record PrewarmRequest(
    PrewarmTriggerSource Source,
    Guid? ItemId = null,
    Guid? UserId = null);

/// <summary>
/// A concrete media item selected for reading by the prewarm worker.
/// </summary>
public sealed record PrewarmCandidate(
    MediaBrowser.Controller.Entities.BaseItem Item,
    PrewarmTriggerSource Source,
    Guid? SourceItemId);
