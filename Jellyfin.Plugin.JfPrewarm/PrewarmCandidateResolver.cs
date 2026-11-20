using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JfPrewarm;

/// <summary>
/// Resolves trigger requests to exactly one concrete, file-backed media item.
/// </summary>
public sealed class PrewarmCandidateResolver
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public PrewarmCandidateResolver(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
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
        if (item is null)
        {
            return null;
        }

        var user = request.UserId is Guid userId
            ? _userManager.GetUserById(userId)
            : null;

        BaseItem? candidate = item switch
        {
            Series series => ResolveSeriesContainer(series, user),
            Season season => ResolveSeasonContainer(season, user),
            Folder folder => ResolveFolderContainer(folder, user),
            _ => IsPlayableFile(item) ? item : null
        };

        return candidate is null
            ? null
            : new PrewarmCandidate(candidate, request.Source, item.Id);
    }

    private BaseItem? ResolveSeriesContainer(Series series, User? user)
    {
        var episodes = GetSeriesEpisodes(series, user);
        return ResolveEpisodeSequence(episodes, user, GetThreshold(), null, out _);
    }

    private BaseItem? ResolveSeasonContainer(Season season, User? user)
    {
        var series = season.Series;
        if (series is null)
        {
            var seasonEpisodes = user is null
                ? season.GetEpisodes().OfType<Episode>()
                : season.GetEpisodes(user, new DtoOptions(false), shouldIncludeMissingEpisodes: false)
                    .OfType<Episode>();

            return FirstAvailableEpisode(seasonEpisodes);
        }

        var allSeriesEpisodes = GetSeriesEpisodes(series, user);
        // With no series history, a season detail page should warm the first
        // available episode of that season, not always S01E01.
        var episodesInSeason = user is null
            ? season.GetEpisodes().OfType<Episode>()
            : season.GetEpisodes(user, new DtoOptions(false), shouldIncludeMissingEpisodes: false)
                .OfType<Episode>();

        var historyCandidate = ResolveEpisodeSequence(
            allSeriesEpisodes,
            user,
            GetThreshold(),
            episodesInSeason,
            out var hasHistory);

        return hasHistory
            ? historyCandidate
            : FirstAvailableEpisode(episodesInSeason);
    }

    private BaseItem? ResolveFolderContainer(Folder folder, User? user)
    {
        var visited = new HashSet<Guid>();
        return FindFirstPlayableDescendant(folder, user, visited);
    }

    private BaseItem? FindFirstPlayableDescendant(
        Folder folder,
        User? user,
        HashSet<Guid> visited)
    {
        if (!visited.Add(folder.Id) || visited.Count > 512)
        {
            return null;
        }

        IReadOnlyList<BaseItem> children;
        if (user is not null)
        {
            children = folder.GetChildren(user, includeLinkedChildren: true);
        }
        else if (folder is BoxSet boxSet)
        {
            children = boxSet.GetLinkedChildren();
        }
        else
        {
            children = folder.Children.ToArray();
        }

        foreach (var child in children)
        {
            if (child is Series series)
            {
                var episode = ResolveSeriesContainer(series, user);
                if (episode is not null)
                {
                    return episode;
                }

                continue;
            }

            if (child is Season season)
            {
                var episode = ResolveSeasonContainer(season, user);
                if (episode is not null)
                {
                    return episode;
                }

                continue;
            }

            if (child is Folder nestedFolder)
            {
                var nestedCandidate = FindFirstPlayableDescendant(nestedFolder, user, visited);
                if (nestedCandidate is not null)
                {
                    return nestedCandidate;
                }

                continue;
            }

            if (IsPlayableFile(child))
            {
                return child;
            }
        }

        return null;
    }

    private List<Episode> GetSeriesEpisodes(Series series, User? user)
    {
        return series.GetEpisodes(user!, new DtoOptions(false), shouldIncludeMissingEpisodes: false)
            .OfType<Episode>()
            .Where(IsPlayableFile)
            .OrderBy(GetSeasonNumber)
            .ThenBy(GetEpisodeNumber)
            .ThenBy(episode => episode.PremiereDate ?? DateTime.MaxValue)
            .ThenBy(episode => episode.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private BaseItem? ResolveEpisodeSequence(
        IReadOnlyList<Episode> episodes,
        User? user,
        int thresholdPercent,
        IEnumerable<Episode>? firstEpisodeFallback,
        out bool hasHistory)
    {
        var availableEpisodes = episodes.Where(IsPlayableFile).ToList();
        hasHistory = false;

        if (availableEpisodes.Count == 0)
        {
            return FirstAvailableEpisode(firstEpisodeFallback ?? []);
        }

        if (user is null)
        {
            return FirstAvailableEpisode(firstEpisodeFallback ?? availableEpisodes);
        }

        var userData = _userDataManager.GetUserDataBatch(
            availableEpisodes.Cast<BaseItem>().ToList(),
            user);

        var lastWatched = availableEpisodes
            .Select((episode, index) => new
            {
                Episode = episode,
                Index = index,
                Data = userData.TryGetValue(episode.Id, out var data) ? data : null
            })
            .Where(entry => entry.Data is not null && HasPlaybackHistory(entry.Data))
            .OrderByDescending(entry => entry.Data!.LastPlayedDate ?? DateTime.MinValue)
            .ThenByDescending(entry => entry.Index)
            .FirstOrDefault();

        if (lastWatched is null)
        {
            return FirstAvailableEpisode(firstEpisodeFallback ?? availableEpisodes);
        }

        hasHistory = true;
        var completed = HasReachedThreshold(lastWatched.Episode, lastWatched.Data!, thresholdPercent);
        if (!completed)
        {
            return lastWatched.Episode;
        }

        for (var index = lastWatched.Index + 1; index < availableEpisodes.Count; index++)
        {
            if (IsPlayableFile(availableEpisodes[index]))
            {
                return availableEpisodes[index];
            }
        }

        return null;
    }

    private static BaseItem? FirstAvailableEpisode(IEnumerable<Episode> episodes)
    {
        return episodes
            .Where(IsPlayableFile)
            .OrderBy(GetSeasonNumber)
            .ThenBy(GetEpisodeNumber)
            .ThenBy(episode => episode.PremiereDate ?? DateTime.MaxValue)
            .ThenBy(episode => episode.SortName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool HasPlaybackHistory(UserItemData data)
    {
        return data.Played || data.PlaybackPositionTicks > 0 || data.LastPlayedDate.HasValue;
    }

    private static bool HasReachedThreshold(Episode episode, UserItemData data, int thresholdPercent)
    {
        if (data.Played)
        {
            return true;
        }

        if (episode.RunTimeTicks is not > 0 || data.PlaybackPositionTicks <= 0)
        {
            return false;
        }

        var thresholdTicks = episode.RunTimeTicks.Value * (long)thresholdPercent / 100L;
        return data.PlaybackPositionTicks >= thresholdTicks;
    }

    private static bool IsPlayableFile(BaseItem item)
    {
        return !item.IsFolder
            && !item.IsVirtualItem
            && !string.IsNullOrWhiteSpace(item.Path);
    }

    private static int GetSeasonNumber(Episode episode)
    {
        return episode.ParentIndexNumber ?? int.MaxValue;
    }

    private static int GetEpisodeNumber(Episode episode)
    {
        return episode.IndexNumber ?? int.MaxValue;
    }

    private static int GetThreshold()
    {
        return Math.Clamp(Plugin.Instance.Configuration.TriggerPercent, 1, 99);
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
            .Where(IsPlayableFile)
            .OrderBy(GetSeasonNumber)
            .ThenBy(GetEpisodeNumber)
            .ThenBy(candidate => candidate.PremiereDate ?? DateTime.MaxValue)
            .FirstOrDefault();

        return nextEpisode is null
            ? null
            : new PrewarmCandidate(nextEpisode, request.Source, sourceEpisode.Id);
    }

    private static bool IsAfter(Episode candidate, Episode current)
    {
        var candidateSeason = GetSeasonNumber(candidate);
        var currentSeason = GetSeasonNumber(current);
        if (candidateSeason != currentSeason)
        {
            return candidateSeason > currentSeason;
        }

        var candidateEpisode = GetEpisodeNumber(candidate);
        var currentEpisode = GetEpisodeNumber(current);
        if (candidateEpisode != currentEpisode)
        {
            return candidateEpisode > currentEpisode;
        }

        return candidate.PremiereDate > current.PremiereDate;
    }
}
