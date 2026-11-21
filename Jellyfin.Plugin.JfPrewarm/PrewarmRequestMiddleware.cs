using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JfPrewarm;

public sealed class PrewarmStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<PrewarmRequestMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Observes successful single-item metadata requests without reading or
/// modifying request/response bodies.
/// </summary>
public sealed class PrewarmRequestMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PrewarmRequestMiddleware> _logger;

    public PrewarmRequestMiddleware(
        RequestDelegate next,
        ILogger<PrewarmRequestMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var match = TryMatch(context.Request);
        await _next(context).ConfigureAwait(false);

        if (match is null)
        {
            return;
        }

        // Authentication may run later in Jellyfin's pipeline, so inspect
        // the authenticated principal only after downstream middleware ran.
        var userId = match.Value.UserId ?? ParseAuthenticatedUserId(context);
        var authenticated = context.User.Identity?.IsAuthenticated ?? false;
        var authenticationType = context.User.Identity?.AuthenticationType ?? "(none)";
        var claimTypes = string.Join(
            ",",
            context.User.Claims.Select(claim => claim.Type).Distinct(StringComparer.Ordinal));

        // Only log the query keys and the non-secret item/user identifiers.
        // Never dump the complete query string because it may contain api_key.
        var queryKeys = string.Join(",", context.Request.Query.Keys);
        var queryUserId = context.Request.Query.TryGetValue("userId", out var userValues)
            ? userValues.ToString()
            : "(absent)";
        var queryIds = context.Request.Query.TryGetValue("ids", out var idValues)
            ? idValues.ToString()
            : "(absent)";

        _logger.LogInformation(
            "ItemDetail observed: {Method} {Path}; QueryKeys=[{QueryKeys}], QueryUserId={QueryUserId}, QueryIds={QueryIds}, Authenticated={Authenticated}, AuthenticationType={AuthenticationType}, ClaimTypes=[{ClaimTypes}], ResolvedUserId={UserId}, Status={StatusCode}, TraceId={TraceId}.",
            context.Request.Method,
            context.Request.Path,
            queryKeys,
            queryUserId,
            queryIds,
            authenticated,
            authenticationType,
            claimTypes,
            userId,
            context.Response.StatusCode,
            context.TraceIdentifier);

        if (context.Response.StatusCode is < 200 or >= 300)
        {
            _logger.LogDebug(
                "ItemDetail request was not queued because the response status was {StatusCode}.",
                context.Response.StatusCode);
            return;
        }

        var queued = context.RequestServices.GetService<PrewarmService>()
            ?.QueueDetailRequest(match.Value.ItemId, userId) ?? false;

        _logger.LogInformation(
            "ItemDetail trigger {QueueResult} for item {ItemId}, user {UserId}, trace {TraceId}.",
            queued ? "queued" : "suppressed as duplicate or unavailable",
            match.Value.ItemId,
            userId,
            context.TraceIdentifier);
    }

    private static (Guid ItemId, Guid? UserId)? TryMatch(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return null;
        }

        var segments = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments is { Length: 2 }
            && string.Equals(segments[0], "Items", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[1], out var itemId))
        {
            return (itemId, ParseQueryUserId(request));
        }

        if (segments is { Length: 4 }
            && string.Equals(segments[0], "Users", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[1], out var routeUserId)
            && string.Equals(segments[2], "Items", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[3], out var legacyItemId))
        {
            return (legacyItemId, routeUserId);
        }

        // Do not treat GET /Items?ids=... as a detail-page trigger.
        // Jellyfin clients use this batch metadata endpoint while rendering
        // lists and other UI surfaces; it can enumerate unrelated library items.
        return null;
    }

    private static Guid? ParseQueryUserId(HttpRequest request)
    {
        return request.Query.TryGetValue("userId", out var values)
            && values.Count == 1
            && Guid.TryParse(values[0], out var userId)
            && userId != Guid.Empty
                ? userId
                : null;
    }

    private static Guid? ParseAuthenticatedUserId(HttpContext context)
    {
        var value = context.User.FindFirst("Jellyfin-UserId")?.Value;
        return Guid.TryParse(value, out var userId) && userId != Guid.Empty
            ? userId
            : null;
    }
}
