using Microsoft.OpenApi.Models;
using RoadDefectDetection.Configuration;
using RoadDefectDetection.Middleware;
using RoadDefectDetection.Services;
using RoadDefectDetection.Services.Interfaces;

namespace RoadDefectDetection
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // ── Controllers & API Explorer ───────────────────────
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();

            // ── Swagger ──────────────────────────────────────────
            builder.Services.AddSwaggerGen(o =>
            {
                o.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title = "Road Defect Detection API",
                    Version = "v1",
                    Description = "Analyzes road images and videos for surface defects " +
                                  "using YOLO ONNX models with OpenCV-based video pipeline."
                });
            });

            // ── Strongly-typed configuration ─────────────────────
            builder.Services.Configure<DetectionSettings>(
                builder.Configuration.GetSection("DetectionSettings"));

            builder.Services.Configure<VideoProcessingSettings>(
                builder.Configuration.GetSection("VideoProcessing"));

            // ── Detection services ────────────────────────────────
            builder.Services.AddSingleton<IDetectionService>(sp =>
                new RoadDetectionService(
                    builder.Configuration,
                    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DetectionSettings>>(),
                    sp.GetRequiredService<ILogger<RoadDetectionService>>()));

            builder.Services.AddSingleton<IExternalMappingService, ExternalMappingService>();

            // ── Video services ────────────────────────────────────
            builder.Services.AddSingleton<AnnotatedVideoCache>();
            builder.Services.AddSingleton<IVideoDetectionService, VideoDetectionService>();

            // ── Kestrel limits ────────────────────────────────────
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Limits.MaxRequestBodySize = 524_288_000; // 500 MB
            });

            // ── CORS ──────────────────────────────────────────────
            builder.Services.AddCors(o =>
            {
                o.AddPolicy("AllowAll", p =>
                    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
            });

            var app = builder.Build();

            // ── Middleware pipeline ───────────────────────────────
            app.UseMiddleware<CorrelationIdMiddleware>();
            app.UseMiddleware<ExceptionMiddleware>();
            app.UseCors("AllowAll");

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI(o =>
                {
                    o.SwaggerEndpoint("/swagger/v1/swagger.json",
                        "Road Defect Detection API v1");
                    o.RoutePrefix = "swagger";
                });
            }

            app.UseHttpsRedirection();
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.UseAuthorization();
            app.MapControllers();
            app.MapFallbackToFile("index.html");

            // ── Startup health summary ────────────────────────────
            var log = app.Services.GetRequiredService<ILogger<Program>>();
            log.LogInformation("Road Defect Detection API starting...");

            var det = app.Services.GetRequiredService<IDetectionService>();
            log.LogInformation(det.IsHealthy()
                ? "Detection service: healthy."
                : "WARNING: No ONNX models loaded. Place .onnx files in /Models and restart.");

            var mapper = app.Services.GetRequiredService<IExternalMappingService>();
            var mappings = mapper.GetAllMappings();
            log.LogInformation(
                "External mapping service: {Count} mapping(s) across {Models} model(s).",
                mappings.Count,
                mappings.Select(m => m.ModelId).Distinct().Count());

            var vid = app.Services.GetRequiredService<IVideoDetectionService>();
            log.LogInformation(vid.IsAvailable()
                ? "OpenCV video pipeline: available."
                : "WARNING: OpenCV native library not found. " +
                  "Install OpenCvSharp4.runtime.win or .ubuntu package.");

            app.Run();
        }
    }
}