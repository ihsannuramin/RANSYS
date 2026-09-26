using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Ransys.Api.Errors;
using Ransys.Api.Security;

namespace Ransys.Api.Composition;

/// <summary>
/// Configuration section <c>Ransys:RateLimiting</c>. A placeholder hook (OpenAPI v1: 429 "rejected by pre-provider rate
/// limiting/backpressure"): a fixed window per authenticated channel, <b>disabled by default</b>. Limits per merchant,
/// product or provider belong to the Configuration Schema v1 (TODO).
/// </summary>
public sealed class RateLimitingOptions
{
    public const string Section = "Ransys:RateLimiting";

    public bool Enabled { get; set; }

    public int PermitLimit { get; set; } = 100;

    public int WindowSeconds { get; set; } = 1;
}

public static class RateLimiting
{
    /// <summary>Registers the limiter when enabled; returns whether the middleware must be added.</summary>
    public static bool AddRansysRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(RateLimitingOptions.Section).Get<RateLimitingOptions>() ?? new RateLimitingOptions();
        if (!options.Enabled)
        {
            return false;
        }

        if (options.PermitLimit <= 0 || options.WindowSeconds <= 0)
        {
            throw new InvalidOperationException($"{RateLimitingOptions.Section}: PermitLimit and WindowSeconds must be positive.");
        }

        services.AddRateLimiter(limiter =>
        {
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                // Runs after RequestAuthenticationMiddleware: only authenticated /api/v1 requests are limited, per channel.
                if (!context.Request.Path.StartsWithSegments("/api/v1", StringComparison.Ordinal))
                {
                    return RateLimitPartition.GetNoLimiter("unlimited");
                }

                var channel = RequestAuthenticationMiddleware.GetClient(context).ChannelId.ToString();
                return RateLimitPartition.GetFixedWindowLimiter(channel, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    QueueLimit = 0,
                });
            });
            limiter.OnRejected = (rejected, _) => new ValueTask(ApiError.TooManyRequests().WriteAsync(rejected.HttpContext));
        });
        return true;
    }
}
