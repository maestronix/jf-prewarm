using System.Collections.Concurrent;
using System.Threading.Channels;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JfPrewarm;

/// <summary>
/// Receives playback progress events and performs preloads away from playback threads.
/// </summary>
public sealed class PrewarmService : BackgroundService
{
    private const int ReadSize = 1024 * 1024;
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(6);

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PrewarmService> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    // An item remains pending from enqueue until its worker attempt has completed.
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();
    private readonly object _stateLock = new();

    public PrewarmService(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ILogger<PrewarmService> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStart += OnPlaybackProgress;
        _logger.LogInformation("Episode Prewarm started. Trigger threshold: {Threshold}%.", GetThreshold());
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStart -= OnPlaybackProgress;
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    private static int GetThreshold()
    {
        return Math.Clamp(Plugin.Instance.Configuration.TriggerPercent, 1, 99);
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs args)
    {
        try
        {
            if (args.Item is not Episode episode
                || args.PlaybackPositionTicks <= 0
                || episode.RunTimeTicks is not > 0)
            {
                return;
            }

            var thresholdTicks = episode.RunTimeTicks.Value * (long)GetThreshold() / 100L;
            if (args.PlaybackPositionTicks < thresholdTicks)
            {
                return;
            }

            if (_pending.TryAdd(episode.Id, 0) && !_queue.Writer.TryWrite(episode.Id))
            {
                _pending.TryRemove(episode.Id, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue episode prewarm from playback progress.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var episodeId in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessEpisodeAsync(episodeId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled prewarm worker error for source episode {EpisodeId}.", episodeId);
            }
            finally
            {
                _pending.TryRemove(episodeId, out _);
            }
        }
    }

    private async Task ProcessEpisodeAsync(Guid sourceEpisodeId, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(sourceEpisodeId) is not Episode sourceEpisode
            || sourceEpisode.SeriesId is not Guid seriesId)
        {
            return;
        }

        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            SeriesId = seriesId,
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
            return;
        }

        var itemKey = nextEpisode.Id.ToString("N");
        var now = DateTime.UtcNow;

        lock (_stateLock)
        {
            var config = Plugin.Instance.Configuration;
            if (config.LastPreloadUtc.TryGetValue(itemKey, out var lastPreload)
                && now - lastPreload < Cooldown)
            {
                return;
            }

            config.PreloadAttempts++;
            config.LastAttemptUtc = now;
            config.LastResult = $"Reading {Describe(nextEpisode)}";
            Plugin.Instance.SaveConfiguration();
        }

        try
        {
            var bytesRead = await ReadFirstMiBAsync(nextEpisode.Path, cancellationToken).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                throw new IOException("The target episode returned no readable bytes.");
            }

            lock (_stateLock)
            {
                var config = Plugin.Instance.Configuration;
                config.LastPreloadUtc[itemKey] = DateTime.UtcNow;
                config.SuccessfulPreloads++;
                config.BytesRead += bytesRead;
                config.LastResult = $"Preloaded {Describe(nextEpisode)} ({bytesRead:N0} bytes).";
                Plugin.Instance.SaveConfiguration();
            }

            _logger.LogInformation(
                "Preloaded next episode {EpisodeName} ({BytesRead} bytes) after {SourceEpisode}.",
                nextEpisode.Name,
                bytesRead,
                sourceEpisode.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_stateLock)
            {
                var config = Plugin.Instance.Configuration;
                config.FailedPreloads++;
                config.LastResult = $"Failed {Describe(nextEpisode)}: {ex.Message}";
                Plugin.Instance.SaveConfiguration();
            }

            _logger.LogWarning(ex, "Preload failed for next episode {EpisodeName} at {Path}.", nextEpisode.Name, nextEpisode.Path);
        }
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

    private static string Describe(Episode episode)
    {
        var season = episode.ParentIndexNumber?.ToString("D2") ?? "??";
        var number = episode.IndexNumber?.ToString("D2") ?? "??";
        return $"{episode.SeriesName} S{season}E{number} - {episode.Name}";
    }

    private static async Task<int> ReadFirstMiBAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var buffer = new byte[ReadSize];
        var totalRead = 0;

        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead),
                cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }
}
