using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RoadDefectDetection.Configuration;

namespace RoadDefectDetection.Services
{
    /// <summary>
    /// Thread-safe in-memory cache for annotated video files.
    /// Enforces:
    ///   - Per-entry expiry (configurable, default 30 min)
    ///   - Maximum entry count (default 10)
    ///   - Maximum total cached size in MB (default 500 MB)
    /// 
    /// When any limit is exceeded the oldest entries are evicted first.
    /// </summary>
    public sealed class AnnotatedVideoCache : IDisposable
    {
        private readonly ConcurrentDictionary<string, CachedVideo> _cache = new();
        private readonly Timer _cleanupTimer;
        private readonly ILogger<AnnotatedVideoCache> _logger;
        private readonly TimeSpan _expiry;
        private readonly int _maxEntries;
        private readonly long _maxTotalBytes;

        // Serialises eviction decisions to avoid races when multiple
        // large videos arrive simultaneously.
        private readonly SemaphoreSlim _evictionLock = new(1, 1);

        public AnnotatedVideoCache(
            IOptions<VideoProcessingSettings> options,
            ILogger<AnnotatedVideoCache> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            var settings = options?.Value ?? new VideoProcessingSettings();
            _expiry = TimeSpan.FromMinutes(Math.Max(1, settings.CacheExpiryMinutes));
            _maxEntries = Math.Max(1, settings.MaxCacheEntries);
            _maxTotalBytes = (long)Math.Max(1, settings.MaxCacheTotalSizeMB) * 1024 * 1024;

            _cleanupTimer = new Timer(
                CleanupExpired, null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));

            _logger.LogInformation(
                "AnnotatedVideoCache: expiry={Expiry}, maxEntries={Entries}, maxTotal={Total}MB",
                _expiry, _maxEntries, settings.MaxCacheTotalSizeMB);
        }

        /// <summary>
        /// Stores an annotated video and returns its unique cache ID.
        /// Evicts oldest entries if size or count limits are breached.
        /// </summary>
        public string Store(byte[] videoBytes, string originalName, string contentType = "video/mp4")
        {
            ArgumentNullException.ThrowIfNull(videoBytes);

            string id = Guid.NewGuid().ToString("N")[..12];
            var entry = new CachedVideo
            {
                Id = id,
                Data = videoBytes,
                OriginalName = originalName,
                ContentType = contentType,
                CreatedAt = DateTime.UtcNow
            };

            _cache[id] = entry;
            _logger.LogInformation(
                "Cached annotated video '{Id}' ({Size:F1}MB) for '{Name}'.",
                id, videoBytes.Length / (1024.0 * 1024.0), originalName);

            // Evict if needed (best-effort, async)
            _ = Task.Run(EnforceCapacityAsync);

            return id;
        }

        /// <summary>
        /// Retrieves a cached video by ID. Returns null if not found or expired.
        /// </summary>
        public CachedVideo? Get(string id)
        {
            if (_cache.TryGetValue(id, out var cached))
            {
                if (DateTime.UtcNow - cached.CreatedAt < _expiry)
                    return cached;

                _cache.TryRemove(id, out _);
                _logger.LogDebug("Video '{Id}' expired and removed.", id);
            }
            return null;
        }

        public bool Remove(string id) => _cache.TryRemove(id, out _);

        // ── Private helpers ──────────────────────────────────────

        private async Task EnforceCapacityAsync()
        {
            await _evictionLock.WaitAsync();
            try
            {
                // Remove expired first
                var now = DateTime.UtcNow;
                var expired = _cache.Where(kvp => now - kvp.Value.CreatedAt >= _expiry)
                                    .Select(kvp => kvp.Key).ToList();
                foreach (var k in expired) _cache.TryRemove(k, out _);

                // Then enforce count and size, oldest first
                while (true)
                {
                    long totalBytes = _cache.Values.Sum(v => (long)v.Data.Length);
                    int totalCount = _cache.Count;

                    if (totalCount <= _maxEntries && totalBytes <= _maxTotalBytes)
                        break;

                    var oldest = _cache.Values
                        .OrderBy(v => v.CreatedAt)
                        .FirstOrDefault();

                    if (oldest == null) break;

                    if (_cache.TryRemove(oldest.Id, out _))
                    {
                        _logger.LogInformation(
                            "Evicted cached video '{Id}' ({Size:F1}MB) to enforce capacity limits.",
                            oldest.Id, oldest.Data.Length / (1024.0 * 1024.0));
                    }
                }
            }
            finally
            {
                _evictionLock.Release();
            }
        }

        private void CleanupExpired(object? state)
        {
            var now = DateTime.UtcNow;
            int removed = 0;
            foreach (var kvp in _cache)
            {
                if (now - kvp.Value.CreatedAt >= _expiry &&
                    _cache.TryRemove(kvp.Key, out _))
                    removed++;
            }
            if (removed > 0)
                _logger.LogInformation(
                    "Cleanup: removed {Count} expired video(s). Remaining: {R}",
                    removed, _cache.Count);
        }

        public void Dispose()
        {
            _cleanupTimer.Dispose();
            _evictionLock.Dispose();
            _cache.Clear();
        }
    }

    public class CachedVideo
    {
        public string Id { get; set; } = string.Empty;
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public string OriginalName { get; set; } = string.Empty;
        public string ContentType { get; set; } = "video/mp4";
        public DateTime CreatedAt { get; set; }
    }
}