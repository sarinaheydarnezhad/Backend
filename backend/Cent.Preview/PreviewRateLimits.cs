using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;

internal static class PreviewRateLimits
{
    public static void Configure(RateLimiterOptions options)
    {
        // The shared host supplies this response; the standalone preview must do
        // so as well. ASP.NET's default rejection is an empty 503.
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, ct) => {
            var response = context.HttpContext.Response;
            response.StatusCode = StatusCodes.Status429TooManyRequests;
            response.Headers.CacheControl = "private, no-store, max-age=0";
            var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
                ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : 60;
            response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            await response.WriteAsJsonAsync(new { code = "rate_limited", message = "Too many requests. Please retry after the indicated delay." }, ct);
        };
    }
}
