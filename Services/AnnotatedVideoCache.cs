using System.Collections.Concurrent;

namespace RoadDefectDetection.Services
{
    /// <summary>
    /// Thread-safe in-memory cache for annotated video files.
    /// Videos are stored temporarily and cleaned up after expiry.
    /// 
    /// In production, you'd use blob storage or disk-based caching.
    /// For this project, in-memory is fine for reasonable video sizes.
    /// </summary>
    public sealed class AnnotatedVideoCache : IDisposable
    {
        private readonly ConcurrentDictionary<string, CachedVideo> _cache = new();
        private readonly Timer _cleanupTimer;
        private readonly ILogger<AnnotatedVideoCache> _logger;
        private readonly TimeSpan _expiry = TimeSpan.FromMinutes(30);

        public AnnotatedVideoCache(ILogger<AnnotatedVideoCache> logger)
        {
            _logger = logger;

            // Run cleanup every 5 minutes
            _cleanupTimer = new Timer(
                CleanupExpired, null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));
        }

        /// <summary>
        /// Stores an annotated video and returns its unique ID.
        /// </summary>
        public string Store(byte[] videoBytes, string originalName, string contentType = "video/mp4")
        {
            string id = Guid.NewGuid().ToString("N")[..12];

            var cached = new CachedVideo
            {
                Id = id,
                Data = videoBytes,
                OriginalName = originalName,
                ContentType = contentType,
                CreatedAt = DateTime.UtcNow
            };

            _cache[id] = cached;

            _logger.LogInformation(
                "Cached annotated video '{Id}' ({Size:F1}MB) for '{Name}'. Cache size: {Count}",
                id, videoBytes.Length / (1024.0 * 1024.0), originalName, _cache.Count);

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
                {
                    return cached;
                }

                // Expired
                _cache.TryRemove(id, out _);
                _logger.LogDebug("Video '{Id}' expired.", id);
            }

            return null;
        }

        /// <summary>
        /// Removes a specific video from cache.
        /// </summary>
        public bool Remove(string id)
        {
            return _cache.TryRemove(id, out _);
        }

        private void CleanupExpired(object? state)
        {
            int removed = 0;
            foreach (var kvp in _cache)
            {
                if (DateTime.UtcNow - kvp.Value.CreatedAt >= _expiry)
                {
                    if (_cache.TryRemove(kvp.Key, out _))
                        removed++;
                }
            }

            if (removed > 0)
            {
                _logger.LogInformation(
                    "Cleaned up {Count} expired video(s). Remaining: {Remaining}",
                    removed, _cache.Count);
            }
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
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