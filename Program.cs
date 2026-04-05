using Microsoft.OpenApi.Models;
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

            // ── Controllers & API Explorer ──────────────────────
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();

            // ── Swagger ─────────────────────────────────────────
            builder.Services.AddSwaggerGen(options =>
            {
                options.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title = "Road Defect Detection API",
                    Version = "v1",
                    Description =
                        "Analyzes road images and videos for surface defects " +
                        "using YOLO models. Supports object tracking, " +
                        "annotated video output, and external system class mapping."
                });
            });

            // ── Detection Services ──────────────────────────────
            builder.Services.AddSingleton<IDetectionService>(provider =>
            {
                var config = builder.Configuration;
                var logger = provider.GetRequiredService<ILogger<RoadDetectionService>>();
                return new RoadDetectionService(config, logger);
            });

            // ── External Mapping Service ────────────────────────
            builder.Services.AddSingleton<IExternalMappingService>(provider =>
            {
                var config = builder.Configuration;
                var logger = provider.GetRequiredService<ILogger<ExternalMappingService>>();
                return new ExternalMappingService(config, logger);
            });

            // Video cache (singleton — lives for app lifetime)
            builder.Services.AddSingleton<AnnotatedVideoCache>();

            // Video detection (singleton)
            builder.Services.AddSingleton<IVideoDetectionService, VideoDetectionService>();

            // ── Kestrel — 500MB max request ─────────────────────
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = 524_288_000;
            });

            // ── CORS ────────────────────────────────────────────
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowAll", policy =>
                {
                    policy.AllowAnyOrigin()
                          .AllowAnyMethod()
                          .AllowAnyHeader();
                });
            });

            var app = builder.Build();

            // ── Middleware Pipeline ──────────────────────────────
            app.UseMiddleware<ExceptionMiddleware>();
            app.UseCors("AllowAll");

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI(o =>
                {
                    o.SwaggerEndpoint("/swagger/v1/swagger.json", "Road Defect Detection API v1");
                    o.RoutePrefix = "swagger";
                });
            }

            app.UseHttpsRedirection();
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.UseAuthorization();
            app.MapControllers();
            app.MapFallbackToFile("index.html");

            // ── Startup Checks ──────────────────────────────────
            var log = app.Services.GetRequiredService<ILogger<Program>>();
            log.LogInformation("Road Defect Detection API starting...");

            var det = app.Services.GetRequiredService<IDetectionService>();
            log.LogInformation(det.IsHealthy()
                ? "Detection service healthy."
                : "WARNING: No models loaded.");

            var mapper = app.Services.GetRequiredService<IExternalMappingService>();
            var mappings = mapper.GetAllMappings();
            log.LogInformation(
                "External mapping service: {Count} mapping(s) configured across {Models} model(s).",
                mappings.Count,
                mappings.Select(m => m.ModelId).Distinct().Count());

            var vid = app.Services.GetRequiredService<IVideoDetectionService>();
            var ffmpegOk = vid.IsAvailableAsync().GetAwaiter().GetResult();
            log.LogInformation(ffmpegOk
                ? "FFmpeg detected. Video + annotated output available."
                : "FFmpeg NOT found. Video features unavailable.");

            app.Run();
        }
    }
}