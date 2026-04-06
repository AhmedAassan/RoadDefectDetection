namespace RoadDefectDetection.Middleware
{
    /// <summary>
    /// Attaches a correlation ID to every request for distributed tracing.
    /// 
    /// If the incoming request contains an "X-Correlation-ID" header,
    /// that value is reused. Otherwise a new GUID is generated.
    /// The correlation ID is echoed in the response headers and added
    /// to the logging scope so it appears in every log entry for the request.
    /// </summary>
    public class CorrelationIdMiddleware
    {
        private const string HeaderName = "X-Correlation-ID";
        private readonly RequestDelegate _next;

        public CorrelationIdMiddleware(RequestDelegate next)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
        }

        public async Task InvokeAsync(HttpContext context, ILogger<CorrelationIdMiddleware> logger)
        {
            string correlationId = context.Request.Headers.TryGetValue(HeaderName, out var hv)
                && !string.IsNullOrWhiteSpace(hv)
                ? hv.ToString()
                : Guid.NewGuid().ToString("N")[..12];

            context.Items[HeaderName] = correlationId;
            context.Response.Headers[HeaderName] = correlationId;

            using (logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId
            }))
            {
                await _next(context);
            }
        }
    }
}