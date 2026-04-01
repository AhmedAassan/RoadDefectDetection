using System.Net;
using System.Text.Json;

namespace RoadDefectDetection.Middleware
{
    /// <summary>
    /// Global exception handling middleware. Catches any unhandled exception that
    /// escapes the request pipeline, logs it, and returns a consistent JSON error
    /// response to the client.
    /// 
    /// This prevents raw exception details or stack traces from leaking to callers
    /// while ensuring every error produces a machine-readable response.
    /// 
    /// Must be registered early in the pipeline (before routing/controllers)
    /// via <c>app.UseMiddleware&lt;ExceptionMiddleware&gt;()</c>.
    /// </summary>
    public class ExceptionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionMiddleware> _logger;

        /// <summary>
        /// JSON serialization options for error responses — camelCase property names
        /// for consistency with ASP.NET Core defaults.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        /// <summary>
        /// Initializes the middleware with the next delegate in the pipeline and a logger.
        /// </summary>
        /// <param name="next">The next middleware delegate.</param>
        /// <param name="logger">Logger for recording exception details.</param>
        public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Invokes the next middleware in the pipeline, catching any unhandled exceptions.
        /// </summary>
        /// <param name="context">The HTTP context for the current request.</param>
        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unhandled exception caught by ExceptionMiddleware. " +
                    "Method: {Method}, Path: {Path}, QueryString: {Query}",
                    context.Request.Method,
                    context.Request.Path,
                    context.Request.QueryString);

                await HandleExceptionAsync(context, ex);
            }
        }

        /// <summary>
        /// Writes a standardized JSON error response to the client.
        /// Always returns HTTP 500 Internal Server Error.
        /// </summary>
        /// <param name="context">The HTTP context.</param>
        /// <param name="exception">The caught exception.</param>
        private static async Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            // Prevent writing to an already-started response
            if (context.Response.HasStarted)
            {
                return;
            }

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

            var errorResponse = new
            {
                success = false,
                message = "An error occurred while processing your request.",
                error = exception.Message
            };

            string json = JsonSerializer.Serialize(errorResponse, JsonOptions);

            await context.Response.WriteAsync(json);
        }
    }
}