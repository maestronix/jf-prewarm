using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.JfPrewarm;

public sealed class PrewarmCandidateResolver
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public PrewarmCandidateResolver(ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager)
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
            PrewarmTriggerSource.SessionStarted => ResolveSessionStarted(request),
            _ => null
        };
    }

    private PrewarmCandidate? ResolveSessionStarted(PrewarmRequest request)
    {
        if (request.UserId is not Guid userId)
        {
            return null;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        // Query a small user-scoped window of recently played episodes and movies.
        // User data is fetched in one batch, not once per media item.
        var recentItems = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie],
            OrderBy = [(ItemSortBy.DatePlayed, SortOrder.Descending)],
            Limit = 10,
            Recursive = true,
            IsVirtualItem = false
        }).Where(IsPlayableFile).ToList();

        if (recentItems.Count == 0)
        {
            return null;
        }

        var userData = _userDataManager.GetUserDataBatch(recentItems, user);
        var latest = recentItems
            .Select((item, index) => new
            {
                Item = item,
                Index = index,
                Data = userData.TryGetValue(item.Id, out var data) ? data : null
            })
            .Where(entry => entry.Data is not null && HasPlaybackHistory(entry.Data))
            .OrderByDescending(entry => entry.Data!.LastPlayedDate ?? DateTime.MinValue)
            .ThenByDescending(entry => entry.Data!.PlaybackPositionTicks > 0)
            .ThenByDescending(entry => entry.Index)
            .FirstOrDefault();

        if (latest is null)
        {
            return null;
        }

        if (latest.Item is Movie movie)
        {
            if (HasReachedThreshold(movie, latest.Data!, GetThreshold()))
            {
                return null;
            }

            return new PrewarmCandidate(movie, request.Source, movie.Id);
        }

        if (latest.Item is not Episode episode || episode.SeriesId == Guid.Empty)
        {
            return null;
        }

        var series = _libraryManager.GetItemById(episode.SeriesId) as Series;
        if (series is null)
        {
            return null;
        }

        var episodes = GetSeriesEpisodes(series, user);
        var currentIndex = episodes.FindIndex(candidate => candidate.Id == episode.Id);
        if (currentIndex < 0)
        {
            return null;
        }

        var target = HasReachedThreshold(episode, latest.Data!, GetThreshold())
            ? NextAvailableEpisode(episodes, currentIndex)
            : episode;

        return target is null ? null : new PrewarmCandidate(target, request.Source, episode.Id);
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

        var user = request.UserId is Guid userId ? _userManager.GetUserById(userId) : null;
        BaseItem? candidate = item switch
        {
            Series series => ResolveSeriesContainer(series, user),
            Season season => ResolveSeasonContainer(season, user),
            Folder folder => ResolveFolderContainer(folder, user),
            _ => IsPlayableFile(item) ? item : null
        };

        return candidate is null ? null : new PrewarmCandidate(candidate, request.Source, item.Id);
    }

    private BaseItem? ResolveSeriesContainer(Series series, User? user)
    {
        return ResolveEpisodeSequence(GetSeriesEpisodes(series, user), user, GetThreshold(), null, out _);
    }

    private BaseItem? ResolveSeasonContainer(Season season, User? user)
    {
        var series = season.Series;
        if (series is null)
        {
            var seasonEpisodes = user is null
                ? season.GetEpisodes().OfType<Episode>()
                : season.GetEpisodes(user, new DtoOptions(false), shouldIncludeMissingEpisodes: false).OfType<Episode>();
            return FirstAvailableEpisode(seasonEpisodes);
        }

        var allSeriesEpisodes = GetSeriesEpisodes(series, user);
        var episodesInSeason = user is null
            ? season.GetEpisodes().OfType<Episode>()
            : season.GetEpisodes(user, new DtoOptions(false), shouldIncludeMissingEpisodes: false).OfType<Episode>();

        var historyCandidate = ResolveEpisodeSequence(allSeriesEpisodes, user, GetThreshold(), episodesInSeason, out var hasHistory);
        return hasHistory ? historyCandidate : FirstAvailableEpisode(episodesInSeason);
    }

    private BaseItem? ResolveFolderContainer(Folder folder, User? user)
    {
        return FindFirstPlayableDescendant(folder, user, new HashSet<Guid>());
    }

    private BaseItem? FindFirstPlayableDescendant(Folder folder, User? user, HashSet<Guid> visited)
    {
        if (!visited.Add(folder.Id) || visited.Count > 512)
        {
            return null;
        }

        IReadOnlyList<BaseItem> children = user is not null
            ? folder.GetChildren(user, includeLinkedChildren: true)
            : folder is BoxSet boxSet ? boxSet.GetLinkedChildren() : folder.Children.ToArray();

        foreach (var child in children)
        {
            if (child is Series series)
            {
                var episode = ResolveSeriesContainer(series, user);
                if (episode is not null) return episode;
                continue;
            }

            if (child is Season season)
            {
                var episode = ResolveSeasonContainer(season, user);
                if (episode is not null) return episode;
                continue;
            }

            if (child is Folder nestedFolder)
            {
                var nestedCandidate = FindFirstPlayableDescendant(nestedFolder, user, visited);
                if (nestedCandidate is not null) return nestedCandidate;
                continue;
            }

            if (IsPlayableFile(child)) return child;
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

    private BaseItem? ResolveEpisodeSequence(IReadOnlyList<Episode> episodes, User? user, int thresholdPercent, IEnumerable<Episode>? firstEpisodeFallback, out bool hasHistory)
    {
        var availableEpisodes = episodes.Where(IsPlayableFile).ToList();
        hasHistory = false;

        if (availableEpisodes.Count == 0) return FirstAvailableEpisode(firstEpisodeFallback ?? []);
        if (user is null) return FirstAvailableEpisode(firstEpisodeFallback ?? availableEpisodes);

        var userData = _userDataManager.GetUserDataBatch(availableEpisodes.Cast<BaseItem>().ToList(), user);
        var lastWatched = availableEpisodes
            .Select((episode, index) => new
            {
                Episode = episode,
                Index = index,
                Data = userData.TryGetValue(episode.Id, out var data) ? data : null
            })
            .Where(entry => entry.Data is not null && HasPlaybackHistory(entry.Data))
            .OrderByDescending(entry => entry.Data!.LastPlayedDate ?? DateTime.MinValue)
            .ThenByDescending(entry => entry.Data!.PlaybackPositionTicks > 0)
            .ThenByDescending(entry => entry.Index)
            .FirstOrDefault();

        if (lastWatched is null) return FirstAvailableEpisode(firstEpisodeFallback ?? availableEpisodes);

        hasHistory = true;
        return HasReachedThreshold(lastWatched.Episode, lastWatched.Data!, thresholdPercent)
            ? NextAvailableEpisode(availableEpisodes, lastWatched.Index)
            : lastWatched.Episode;
    }

    private static Episode? NextAvailableEpisode(IReadOnlyList<Episode> episodes, int currentIndex)
    {
        for (var index = currentIndex + 1; index < episodes.Count; index++)
        {
            if (IsPlayableFile(episodes[index])) return episodes[index];
        }

        return null;
    }

    private static BaseItem? FirstAvailableEpisode(IEnumerable<Episode> episodes)
    {
        return episodes.Where(IsPlayableFile)
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

    private static bool HasReachedThreshold(BaseItem item, UserItemData data, int thresholdPercent)
    {
        if (data.Played) return true;
        if (item.RunTimeTicks is not > 0 || data.PlaybackPositionTicks <= 0) return false;
        var thresholdTicks = item.RunTimeTicks.Value * (long)thresholdPercent / 100L;
        return data.PlaybackPositionTicks >= thresholdTicks;
    }

    private static bool IsPlayableFile(BaseItem item)
    {
        return !item.IsFolder && !item.IsVirtualItem && !string.IsNullOrWhiteSpace(item.Path);
    }

    private static int GetSeasonNumber(Episode episode) => episode.ParentIndexNumber ?? int.MaxValue;
    private static int GetEpisodeNumber(Episode episode) => episode.IndexNumber ?? int.MaxValue;
    private static int GetThreshold() => Math.Clamp(Plugin.Instance.Configuration.TriggerPercent, 1, 99);

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
        }).OfType<Episode>()
            .Where(IsPlayableFile)
            .OrderBy(GetSeasonNumber)
            .ThenBy(GetEpisodeNumber)
            .ThenBy(candidate => candidate.PremiereDate ?? DateTime.MaxValue)
            .ToList();

        var currentIndex = episodes.FindIndex(candidate => candidate.Id == sourceEpisode.Id);
        var nextEpisode = currentIndex < 0 ? null : NextAvailableEpisode(episodes, currentIndex);
        return nextEpisode is null ? null : new PrewarmCandidate(nextEpisode, request.Source, sourceEpisode.Id);
    }
}