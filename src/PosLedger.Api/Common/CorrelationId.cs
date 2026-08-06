using Serilog.Context;

namespace PosLedger.Api.Common;

/// <summary>
/// Every request carries a correlation id: taken from the caller's <c>X-Correlation-Id</c> when
/// present, generated otherwise. It goes back on the response, into every log line of the request
/// via Serilog's LogContext, and into the <c>traceId</c> of any ProblemDetails the request produces.
/// That last part is the point: a client can report an error id and it can be found in the logs.
/// </summary>
public static class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";
    private const string ItemKey = "CorrelationId";

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var incoming)
                                && !string.IsNullOrWhiteSpace(incoming)
                ? incoming.ToString()
                : Guid.NewGuid().ToString("n");

            context.Items[ItemKey] = correlationId;
            context.Response.Headers[HeaderName] = correlationId;

            using (LogContext.PushProperty(ItemKey, correlationId))
            {
                await next();
            }
        });

    public static string GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string id
            ? id
            : context.TraceIdentifier;
}
