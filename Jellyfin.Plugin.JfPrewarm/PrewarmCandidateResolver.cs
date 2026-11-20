using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JfPrewarm;

/// <summary>
/// Resolves trigger requests to one concrete, file-backed media item.
/// Candidate selection lives here rather than in event handlers.
/// </summary>
public sealed class PrewarmCandidateResolver
{
    private readonly ILibraryManager _libraryManager;

    public PrewarmCandidateResolver(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public PrewarmCandidate? Resolve(PrewarmRequest request)
    {
        return request.Source switch
        {
            PrewarmTriggerSource.PlaybackProgress => ResolvePlaybackProgress(request),
            PrewarmTriggerSource.ItemDetail => ResolveItemDetail(request),
            // Session-start candidate selection will be added in the next phase.
            PrewarmTriggerSource.SessionStarted => null,
            _ => null
        };
    }

    private PrewarmCandidate? ResolveItemDetail(PrewarmRequest request)
    {
        if (request.ItemId is not Guid itemId)
        {
            return null;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || string.IsNullOrWhiteSpace(item.Path))
        {
            return null;
        }

        return new PrewarmCandidate(item, request.Source, item.Id);
    }

    private PrewarmCandidate? ResolvePlaybackProgress(PrewarmRequest request)
    {
        if (request.ItemId is not Guid sourceItemId
            || _libraryManager.GetItemById(sourceItemId) is not Episode sourceEpisode
            || sourceEpisode.SeriesId == Guid.Empty)
        {
            return null;
        }

        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = [sourceEpisode.SeriesId],
            Recursive = true,
            IsVirtualItem = false
        }).OfType<Episode>();

        var nextEpisode = episodes
            .Where(candidate => candidate.Id != sourceEpisode.Id && IsAfter(candidate, sourceEpisode))
            .OrderBy(candidate => candidate.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(candidate => candidate.IndexNumber ?? int.MaxValue)
            .ThenBy(candidate => candidate.PremiereDate ?? DateTime.MaxValue)
            .FirstOrDefault();

        if (nextEpisode is null || string.IsNullOrWhiteSpace(nextEpisode.Path))
        {
            return null;
        }

        return new PrewarmCandidate(nextEpisode, request.Source, sourceEpisode.Id);
    }

    private static bool IsAfter(Episode candidate, Episode current)
    {
        var candidateSeason = candidate.ParentIndexNumber ?? int.MaxValue;
        var currentSeason = current.ParentIndexNumber ?? int.MaxValue;
        if (candidateSeason != currentSeason)
        {
            return candidateSeason > currentSeason;
        }

        var candidateEpisode = candidate.IndexNumber ?? int.MaxValue;
        var currentEpisode = current.IndexNumber ?? int.MaxValue;
        if (candidateEpisode != currentEpisode)
        {
            return candidateEpisode > currentEpisode;
        }

        return candidate.PremiereDate > current.PremiereDate;
    }
}
