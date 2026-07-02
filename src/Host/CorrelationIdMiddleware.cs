using Microsoft.Extensions.Logging;

namespace Ruoyu.Study.DocLibrary.Host;

/// <summary>
/// HTTP CorrelationId middleware: reads or creates a CorrelationId from the x-correlation-id header,
/// injects it into the log context via ILogger.BeginScope, and writes it back to the response header.
/// </summary>
public class CorrelationIdMiddleware
{
    public const string CorrelationIdHeader = "x-correlation-id";
    public const string CorrelationIdLogKey = "CorrelationId";
    public const string HttpContextItemsKey = "__CorrelationId";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = GetOrCreateCorrelationId(context);
        context.Items[HttpContextItemsKey] = correlationId;
        context.Response.Headers[CorrelationIdHeader] = correlationId;

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            [CorrelationIdLogKey] = correlationId
        });

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP request failed: CorrelationId={CorrelationId}, Method={Method}, Path={Path}",
                correlationId, context.Request.Method, context.Request.Path);
            throw;
        }
    }

    private static string GetOrCreateCorrelationId(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("N");
        }
        return correlationId;
    }
}
