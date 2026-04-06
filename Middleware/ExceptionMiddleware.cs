using System.Net;
using System.Text.Json;

namespace RoadDefectDetection.Middleware
{
    /// <summary>
    /// Global exception handler. Catches unhandled exceptions, logs them,
    /// and returns a consistent JSON response.
    /// 
    /// In Development, the exception message is included in the response.
    /// In Production, only a generic message is returned to avoid leaking internals.
    /// </summary>
    public class ExceptionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ExceptionMiddleware> _logger;
        private readonly IHostEnvironment _environment;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public ExceptionMiddleware(
            RequestDelegate next,
            ILogger<ExceptionMiddleware> logger,
            IHostEnvironment environment)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unhandled exception. Method={Method}, Path={Path}, Query={Query}",
                    context.Request.Method,
                    context.Request.Path,
                    context.Request.QueryString);

                await HandleExceptionAsync(context, ex);
            }
        }

        private async Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            if (context.Response.HasStarted) return;

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

            // Only expose the raw exception message in Development
            string detail = _environment.IsDevelopment()
                ? exception.Message
                : "An internal error occurred. Please contact support if the issue persists.";

            var body = new
            {
                success = false,
                message = "An error occurred while processing your request.",
                error = detail
            };

            await context.Response.WriteAsync(
                JsonSerializer.Serialize(body, JsonOptions));
        }
    }
}