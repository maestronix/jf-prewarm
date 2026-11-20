using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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

    public PrewarmRequestMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var match = TryMatch(context.Request);
        await _next(context).ConfigureAwait(false);

        if (match is null || context.Response.StatusCode is < 200 or >= 300)
        {
            return;
        }

        context.RequestServices.GetService<PrewarmService>()
            ?.QueueDetailRequest(match.Value.ItemId, match.Value.UserId);
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
            return (itemId, ParseUserId(request));
        }

        if (segments is { Length: 4 }
            && string.Equals(segments[0], "Users", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[1], out var routeUserId)
            && string.Equals(segments[2], "Items", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[3], out var legacyItemId))
        {
            return (legacyItemId, routeUserId);
        }

        if (segments is { Length: 1 }
            && string.Equals(segments[0], "Items", StringComparison.OrdinalIgnoreCase)
            && request.Query.TryGetValue("ids", out var ids)
            && ids.Count == 1
            && Guid.TryParse(ids[0], out var queryItemId))
        {
            return (queryItemId, ParseUserId(request));
        }

        return null;
    }

    private static Guid? ParseUserId(HttpRequest request)
    {
        return request.Query.TryGetValue("userId", out var values)
            && values.Count == 1
            && Guid.TryParse(values[0], out var userId)
                ? userId
                : null;
    }
}
