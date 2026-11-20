using System.Collections.Concurrent;
using System.Threading.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JfPrewarm;

public sealed class PrewarmService : BackgroundService
{
    private const int ReadSize = 1024 * 1024;

    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(6);

    private readonly ISessionManager _sessionManager;
    private readonly PrewarmCandidateResolver _candidateResolver;
    private readonly ILogger<PrewarmService> _logger;
    private readonly Channel<PrewarmRequest> _queue = Channel.CreateBounded<PrewarmRequest>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private readonly ConcurrentDictionary<PrewarmRequest, byte> _pending = new();
    private readonly object _stateLock = new();

    public PrewarmService(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILogger<PrewarmService> logger)
    {
        _sessionManager = sessionManager;
        _candidateResolver = new PrewarmCandidateResolver(libraryManager, userManager, userDataManager);
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStart += OnPlaybackProgress;
        _sessionManager.SessionStarted += OnSessionStarted;
        _logger.LogInformation("Episode Prewarm started. Trigger threshold: {Threshold}%.", GetThreshold());
        _logger.LogInformation("Global prewarm cooldown: {Cooldown}.", Cooldown);
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStart -= OnPlaybackProgress;
        _sessionManager.SessionStarted -= OnSessionStarted;
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    public bool QueueDetailRequest(Guid itemId, Guid? userId)
    {
        return QueueRequest(new PrewarmRequest(PrewarmTriggerSource.ItemDetail, itemId, userId));
    }

    private static int GetThreshold() => Math.Clamp(Plugin.Instance.Configuration.TriggerPercent, 1, 99);

    private void OnSessionStarted(object? sender, SessionEventArgs args)
    {
        try
        {
            var session = args.SessionInfo;
            if (session.UserId == Guid.Empty) return;

            _logger.LogInformation(
                "New session detected for user {UserId}, client {Client}, device {DeviceId}; queueing session-start resolution.",
                session.UserId,
                session.Client,
                session.DeviceId);

            QueueRequest(new PrewarmRequest(PrewarmTriggerSource.SessionStarted, UserId: session.UserId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue prewarm from session-start event.");
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs args)
    {
        try
        {
            if (args.Item is not Episode episode
                || args.PlaybackPositionTicks is not long positionTicks
                || positionTicks <= 0
                || episode.RunTimeTicks is not > 0)
            {
                return;
            }

            var thresholdTicks = episode.RunTimeTicks.Value * (long)GetThreshold() / 100L;
            if (positionTicks < thresholdTicks) return;
            QueueRequest(new PrewarmRequest(PrewarmTriggerSource.PlaybackProgress, episode.Id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue episode prewarm from playback progress.");
        }
    }

    private bool QueueRequest(PrewarmRequest request)
    {
        if (!_pending.TryAdd(request, 0))
        {
            return false;
        }

        if (!_queue.Writer.TryWrite(request))
        {
            _pending.TryRemove(request, out _);
            return false;
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessRequestAsync(request, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled prewarm worker error for {TriggerSource} request {ItemId}.", request.Source, request.ItemId);
            }
            finally
            {
                _pending.TryRemove(request, out _);
            }
        }
    }

    private async Task ProcessRequestAsync(PrewarmRequest request, CancellationToken cancellationToken)
    {
        PrewarmCandidate? candidate;

        try
        {
            candidate = _candidateResolver.Resolve(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Candidate resolution failed for {TriggerSource} trigger and user {UserId}.",
                request.Source,
                request.UserId);
            throw;
        }

        if (candidate is null)
        {
            if (request.Source == PrewarmTriggerSource.SessionStarted)
            {
                _logger.LogInformation(
                    "No eligible session-start candidate found for user {UserId}.",
                    request.UserId);
            }
            else if (request.Source == PrewarmTriggerSource.ItemDetail)
            {
                _logger.LogInformation(
                    "ItemDetail request for item {ItemId} and user {UserId} produced no eligible preload candidate.",
                    request.ItemId,
                    request.UserId);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(candidate.Item.Path))
        {
            if (request.Source == PrewarmTriggerSource.ItemDetail)
            {
                _logger.LogInformation(
                    "ItemDetail candidate {ItemName} ({ItemId}) has no local file path; skipping preload.",
                    Describe(candidate.Item),
                    candidate.Item.Id);
            }

            return;
        }

        var targetItem = candidate.Item;
        var itemKey = targetItem.Id.ToString("N");
        var now = DateTime.UtcNow;

        _logger.LogInformation(
            "Resolved {TriggerSource} candidate {ItemName} ({ItemId}) for user {UserId}.",
            request.Source,
            Describe(targetItem),
            targetItem.Id,
            request.UserId);

        lock (_stateLock)
        {
            var config = Plugin.Instance.Configuration;
            config.LastPreloads ??= [];
            config.LastPreloads.RemoveAll(entry => now - entry.LastPreloadUtc >= Cooldown);

            var previousPreload = config.LastPreloads.FirstOrDefault(
                entry => string.Equals(entry.EpisodeId, itemKey, StringComparison.OrdinalIgnoreCase));

            if (previousPreload is not null && now - previousPreload.LastPreloadUtc < Cooldown)
            {
                _logger.LogInformation(
                    "Skipping {ItemName}: target is still in the global {Cooldown} cooldown.",
                    Describe(targetItem),
                    Cooldown);
                return;
            }

            config.PreloadAttempts++;
            config.LastAttemptUtc = now;
            config.LastResult = $"Reading {Describe(targetItem)}";
            Plugin.Instance.SaveConfiguration();
        }

        try
        {
            var bytesRead = await ReadFirstMiBAsync(targetItem.Path, cancellationToken).ConfigureAwait(false);
            if (bytesRead <= 0) throw new IOException("The target media item returned no readable bytes.");

            lock (_stateLock)
            {
                var config = Plugin.Instance.Configuration;
                config.LastPreloads ??= [];
                var existingEntry = config.LastPreloads.FirstOrDefault(
                    entry => string.Equals(entry.EpisodeId, itemKey, StringComparison.OrdinalIgnoreCase));

                if (existingEntry is null)
                {
                    config.LastPreloads.Add(new PreloadCooldownEntry { EpisodeId = itemKey, LastPreloadUtc = DateTime.UtcNow });
                }
                else
                {
                    existingEntry.LastPreloadUtc = DateTime.UtcNow;
                }

                config.SuccessfulPreloads++;
                config.BytesRead += bytesRead;
                config.LastResult = $"Preloaded {Describe(targetItem)} ({bytesRead:N0} bytes).";
                Plugin.Instance.SaveConfiguration();
            }

            _logger.LogInformation(
                "Preloaded {ItemName} ({BytesRead} bytes) from {TriggerSource} trigger.",
                Describe(targetItem),
                bytesRead,
                candidate.Source);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_stateLock)
            {
                var config = Plugin.Instance.Configuration;
                config.FailedPreloads++;
                config.LastResult = $"Failed {Describe(targetItem)}: {ex.Message}";
                Plugin.Instance.SaveConfiguration();
            }

            _logger.LogWarning(ex, "Preload failed for {ItemName} at {Path}.", Describe(targetItem), targetItem.Path);
        }
    }

    private static string Describe(BaseItem item)
    {
        if (item is Episode episode)
        {
            var season = episode.ParentIndexNumber?.ToString("D2") ?? "??";
            var number = episode.IndexNumber?.ToString("D2") ?? "??";
            return $"{episode.SeriesName} S{season}E{number} - {episode.Name}";
        }

        return item.Name;
    }

    private static async Task<int> ReadFirstMiBAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var buffer = new byte[ReadSize];
        var totalRead = 0;

        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }

        return totalRead;
    }
}