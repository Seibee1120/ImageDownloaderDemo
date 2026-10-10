using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace ImageDownloaderDemo
{
    /// <summary>
    /// Async remote image loader for Unity. Clean-room rewrite of a production component.
    ///
    /// Highlights:
    ///  - Request de-duplication: concurrent requests for the same URL share a single download.
    ///  - LRU texture cache with both a count limit and an approximate memory budget.
    ///  - Reference counting keeps textures that are still in use from being evicted.
    ///  - Owner lifecycle binding: when the requesting component is destroyed its callbacks are
    ///    dropped and its cache references are released automatically.
    ///  - Bounded concurrency with a pending queue, timeout, retry and cancellation.
    /// </summary>
    [AddComponentMenu("ImageDownloader/Image Downloader")]
    public class ImageDownloader : MonoSingleton<ImageDownloader>
    {
        [Header("Download")]
        [Tooltip("Per-request timeout in seconds. <= 0 disables the manual timeout.")]
        public float timeoutSeconds = 30f;

        [Tooltip("Number of retries after the first failure.")]
        public int maxRetry = 2;

        [Tooltip("Delay between retries in seconds.")]
        public float retryDelaySeconds = 1f;

        [Tooltip("Maximum number of simultaneous downloads. Must be >= 1.")]
        public int maxConcurrentDownloads = 8;

        [Header("Cache")]
        [Tooltip("Maximum number of cached textures. <= 0 disables the count limit.")]
        public int maxCacheCount = 50;

        [Tooltip("Approximate cache memory budget in bytes. <= 0 disables the memory limit.")]
        public long maxCacheMemoryBytes = 100L * 1024 * 1024;

        [Tooltip("Sort query parameters during URL normalization. Leave OFF for signed URLs (e.g. S3).")]
        public bool sortQueryParameters;

        // url -> cached entry
        private readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

        // url -> in-flight (or queued) request
        private readonly Dictionary<string, DownloadRequest> _downloading = new Dictionary<string, DownloadRequest>(StringComparer.Ordinal);

        // url -> number of owners holding a cache reference
        private readonly Dictionary<string, int> _refCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        // Least-recently-used order: first = most recently used.
        private readonly LinkedList<string> _lruList = new LinkedList<string>();

        private readonly Queue<string> _waitQueue = new Queue<string>();
        private long _currentCacheMemoryBytes;

        #region Public API

        /// <summary>
        /// Asynchronously loads a texture. Concurrent calls with the same URL are merged into one
        /// download. A cache hit invokes <paramref name="onComplete"/> synchronously.
        /// </summary>
        /// <param name="owner">
        /// Component using the texture. Its lifetime is tracked: when the owner is destroyed the
        /// callback is dropped and its cache reference released.
        /// </param>
        public void GetTexture(string url, Component owner, Action<Texture2D> onComplete, bool forceRefresh = false)
            => Request(url, owner, entry => onComplete?.Invoke(entry?.Texture), forceRefresh);

        /// <summary>
        /// Asynchronously loads a texture without lifetime binding. Callers must pair
        /// <see cref="RetainCacheRef"/> / <see cref="ReleaseCacheRef"/> themselves if they need the
        /// entry to stay cached.
        /// </summary>
        public void GetTexture(string url, Action<Texture2D> onComplete, bool forceRefresh = false)
            => Request(url, null, entry => onComplete?.Invoke(entry?.Texture), forceRefresh);

        /// <summary>Asynchronously loads a sprite (texture + Sprite cached per pixelsPerUnit).</summary>
        public void GetSprite(string url, Component owner, Action<Sprite> onComplete,
            float pixelsPerUnit = 100f, bool forceRefresh = false)
            => Request(url, owner, entry => onComplete?.Invoke(entry?.GetOrCreateSprite(pixelsPerUnit)), forceRefresh);

        /// <summary>Asynchronously loads a sprite without lifetime binding.</summary>
        public void GetSprite(string url, Action<Sprite> onComplete,
            float pixelsPerUnit = 100f, bool forceRefresh = false)
            => Request(url, null, entry => onComplete?.Invoke(entry?.GetOrCreateSprite(pixelsPerUnit)), forceRefresh);

        /// <summary>Cancel one URL. All waiters are invoked immediately with null. Cached URLs are unaffected.</summary>
        public void Cancel(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            url = NormalizeUrl(url);
            if (!_downloading.TryGetValue(url, out var request) || request.Cancelled) return;

            request.Cancelled = true;

            var waiters = new List<Waiter>(request.Waiters);
            request.Waiters.Clear();
            foreach (var waiter in waiters)
                InvokeWaiter(waiter, null);

            // Not started yet -> drop it. A running coroutine cleans itself up in the next frame.
            if (request.Coroutine == null &&
                _downloading.TryGetValue(url, out var current) && ReferenceEquals(current, request))
                _downloading.Remove(url);
        }

        /// <summary>Cancel every in-flight download.</summary>
        public void CancelAll()
        {
            var urls = new List<string>(_downloading.Keys);
            foreach (var url in urls) Cancel(url);
        }

        /// <summary>Destroy and remove a single cached texture/sprite.</summary>
        public void RemoveFromCache(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            url = NormalizeUrl(url);
            if (_cache.TryGetValue(url, out var entry)) RemoveFromCache(url, entry);
        }

        /// <summary>Destroy and remove every cached texture/sprite.</summary>
        public void ClearCache()
        {
            foreach (var kvp in _cache) kvp.Value.Dispose();
            _cache.Clear();
            _lruList.Clear();
            _refCounts.Clear();
            _currentCacheMemoryBytes = 0;
        }

        /// <summary>Increase the cache reference count for a URL. Pair with <see cref="ReleaseCacheRef"/>.</summary>
        public void RetainCacheRef(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            url = NormalizeUrl(url);
            _refCounts.TryGetValue(url, out var count);
            _refCounts[url] = count + 1;
        }

        /// <summary>Decrease the cache reference count. At zero the entry becomes evictable again.</summary>
        public void ReleaseCacheRef(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            url = NormalizeUrl(url);
            if (!_refCounts.TryGetValue(url, out var count)) return;

            if (count <= 1) _refCounts.Remove(url);
            else _refCounts[url] = count - 1;

            EvictIfNeeded();
        }

        /// <summary>Number of cached textures.</summary>
        public int CacheCount => _cache.Count;

        /// <summary>Approximate cache memory usage in bytes.</summary>
        public long CacheMemoryBytes => _currentCacheMemoryBytes;

        /// <summary>Number of URLs that are downloading or queued.</summary>
        public int DownloadingCount => _downloading.Count;

        /// <summary>Number of downloads actually transferring data right now.</summary>
        public int ActiveDownloadCount { get; private set; }

        /// <summary>Whether a URL is currently cached.</summary>
        public bool IsCached(string url)
            => !string.IsNullOrEmpty(url) && _cache.ContainsKey(NormalizeUrl(url));

        #endregion

        #region Scheduling

        /// <summary>Single entry point: bind owner -> cache hit? -> de-duplicate/queue download.</summary>
        private void Request(string url, Component owner, Action<CacheEntry> onResolved, bool forceRefresh)
        {
            if (string.IsNullOrEmpty(url))
            {
                Debug.LogWarning("[ImageDownloader] URL is null or empty.");
                InvokeWaiter(new Waiter { OnResolved = onResolved }, null);
                return;
            }

            url = NormalizeUrl(url);

            // 1. Bind the owner's lifetime and register a reference.
            TextureRefTracker tracker = null;
            if (owner != null)
            {
                tracker = owner.gameObject.GetComponent<TextureRefTracker>();
                if (tracker == null) tracker = owner.gameObject.AddComponent<TextureRefTracker>();
                tracker.Bind(this);
                tracker.Hold(url);
                RetainCacheRef(url);
            }

            var waiter = new Waiter
            {
                Tracker = tracker,
                HasOwner = tracker != null,
                OnResolved = onResolved
            };

            // 2. Cache hit -> synchronous callback.
            if (!forceRefresh && _cache.TryGetValue(url, out var entry) && entry.Texture != null)
            {
                UpdateLruAccess(entry);
                InvokeWaiter(waiter, entry);
                return;
            }

            // 3. Force refresh -> drop the stale entry first.
            if (forceRefresh && _cache.TryGetValue(url, out var stale)) RemoveFromCache(url, stale);

            // 4. Merge into an existing request, otherwise create one.
            if (_downloading.TryGetValue(url, out var existing) && !existing.Cancelled)
            {
                existing.Waiters.Add(waiter);
                return;
            }

            var request = new DownloadRequest();
            request.Waiters.Add(waiter);
            _downloading[url] = request;

            if (ActiveDownloadCount < Mathf.Max(1, maxConcurrentDownloads)) StartCoroutineFor(url, request);
            else _waitQueue.Enqueue(url);
        }

        private void StartCoroutineFor(string url, DownloadRequest request)
        {
            ActiveDownloadCount++;
            request.Coroutine = StartCoroutine(DownloadRoutine(url, request));
        }

        private void PumpQueue()
        {
            var limit = Mathf.Max(1, maxConcurrentDownloads);
            while (ActiveDownloadCount < limit && _waitQueue.Count > 0)
            {
                var url = _waitQueue.Dequeue();
                if (_downloading.TryGetValue(url, out var request) && !request.Cancelled && request.Coroutine == null)
                    StartCoroutineFor(url, request);
            }
        }

        #endregion

        #region Download coroutine

        private IEnumerator DownloadRoutine(string url, DownloadRequest request)
        {
            try
            {
                var attempt = 0;
                while (true)
                {
                    using (var webRequest = UnityWebRequestTexture.GetTexture(url))
                    {
                        if (timeoutSeconds > 0) webRequest.timeout = Mathf.CeilToInt(timeoutSeconds);

                        var op = webRequest.SendWebRequest();
                        var elapsed = 0f;

                        while (!op.isDone)
                        {
                            if (request.Cancelled)
                            {
                                webRequest.Abort();
                                break;
                            }

                            if (timeoutSeconds > 0)
                            {
                                elapsed += Time.unscaledDeltaTime;
                                if (elapsed >= timeoutSeconds)
                                {
                                    webRequest.Abort();
                                    break;
                                }
                            }

                            yield return null;
                        }

                        if (request.Cancelled)
                        {
                            Finish(url, request, null);
                            yield break;
                        }

                        if (webRequest.result == UnityWebRequest.Result.Success)
                        {
                            Texture2D texture = null;
                            try
                            {
                                texture = DownloadHandlerTexture.GetContent(webRequest);
                            }
                            catch (Exception e)
                            {
                                Debug.LogError($"[ImageDownloader] Failed to decode texture from '{url}': {e}");
                            }

                            if (texture != null)
                            {
                                var entry = AddOrReuseCache(url, texture);
                                Finish(url, request, entry);
                                yield break;
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"[ImageDownloader] Download failed '{url}': {webRequest.error}");
                        }
                    }

                    attempt++;
                    if (attempt > maxRetry)
                    {
                        Finish(url, request, null);
                        yield break;
                    }

                    Debug.LogWarning($"[ImageDownloader] Retry {attempt}/{maxRetry}: '{url}'");
                    yield return new WaitForSecondsRealtime(retryDelaySeconds);
                }
            }
            finally
            {
                ActiveDownloadCount--;
                PumpQueue();
            }
        }

        /// <summary>Common completion path: remove the request and invoke every waiter.</summary>
        private void Finish(string url, DownloadRequest request, CacheEntry entry)
        {
            if (_downloading.TryGetValue(url, out var current) && ReferenceEquals(current, request))
                _downloading.Remove(url);

            var waiters = new List<Waiter>(request.Waiters);
            request.Waiters.Clear();
            foreach (var waiter in waiters)
                InvokeWaiter(waiter, entry);
        }

        private static void InvokeWaiter(Waiter waiter, CacheEntry entry)
        {
            if (waiter == null) return;

            // Owner already destroyed -> drop the callback (avoids touching a dead object / orphan sprite).
            if (waiter.HasOwner && waiter.Tracker == null) return;

            try
            {
                waiter.OnResolved?.Invoke(entry);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ImageDownloader] Consumer callback threw: {e}");
            }
        }

        #endregion

        #region Cache and eviction

        private CacheEntry AddOrReuseCache(string url, Texture2D texture)
        {
            if (_cache.TryGetValue(url, out var existing) && existing.Texture != null)
            {
                // Someone else already populated the cache while we were downloading -> keep theirs.
                UnityEngine.Object.Destroy(texture);
                UpdateLruAccess(existing);
                return existing;
            }

            return AddToCache(url, texture);
        }

        private CacheEntry AddToCache(string url, Texture2D texture)
        {
            var node = new LinkedListNode<string>(url);
            _lruList.AddFirst(node);
            var entry = new CacheEntry { Texture = texture, LruNode = node };
            _cache[url] = entry;
            _currentCacheMemoryBytes += EstimateTextureBytes(texture);

            // Protect the entry we just added: a single oversized texture must not evict itself
            // and hand a destroyed texture back to the caller.
            EvictIfNeeded(url);
            return entry;
        }

        private void EvictIfNeeded(string protectUrl = null)
        {
            while (_lruList.Count > 0)
            {
                var overCount = maxCacheCount > 0 && _cache.Count > maxCacheCount;
                var overMemory = maxCacheMemoryBytes > 0 && _currentCacheMemoryBytes > maxCacheMemoryBytes;
                if (!overCount && !overMemory) break;

                var evicted = false;
                var node = _lruList.Last;
                while (node != null)
                {
                    var url = node.Value;
                    var retained = _refCounts.TryGetValue(url, out var refCount) && refCount > 0;
                    if (!retained && url != protectUrl && _cache.TryGetValue(url, out var entry))
                    {
                        RemoveFromCache(url, entry);
                        evicted = true;
                        break;
                    }

                    node = node.Previous;
                }

                // Everything left is referenced -> nothing to evict right now.
                if (!evicted) break;
            }
        }

        private void RemoveFromCache(string url, CacheEntry entry)
        {
            if (entry.Texture != null)
                _currentCacheMemoryBytes -= EstimateTextureBytes(entry.Texture);

            entry.Dispose();

            if (entry.LruNode?.List != null) _lruList.Remove(entry.LruNode);
            _cache.Remove(url);
        }

        /// <summary>
        /// Approximate texture memory in bytes. Uses the real texture format and just-adds the mip
        /// chain (~1/3) for mipmapped textures. This is an estimate, not a precise allocation size.
        /// </summary>
        private static long EstimateTextureBytes(Texture2D texture)
        {
            if (texture == null) return 0;

            var bitsPerPixel = BitsPerPixel(texture.format);
            long baseBytes = (long)texture.width * texture.height * bitsPerPixel / 8;
            if (baseBytes <= 0) return 0;

            var mipCount = Mathf.Max(1, texture.mipmapCount);
            return mipCount > 1 ? baseBytes * 4 / 3 : baseBytes;
        }

        private static int BitsPerPixel(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.Alpha8:
                case TextureFormat.R8:
                    return 8;
                case TextureFormat.RGB565:
                case TextureFormat.ARGB4444:
                case TextureFormat.RGBA4444:
                    return 16;
                case TextureFormat.RGB24:
                    return 24;
                case TextureFormat.RGBAHalf:
                    return 64;
                case TextureFormat.RGBAFloat:
                    return 128;
                default:
                    // RGBA32 / ARGB32 / BGRA32 and everything else.
                    return 32;
            }
        }

        private void UpdateLruAccess(CacheEntry entry)
        {
            if (entry.LruNode?.List == null || ReferenceEquals(_lruList.First, entry.LruNode)) return;
            _lruList.Remove(entry.LruNode);
            _lruList.AddFirst(entry.LruNode);
        }

        #endregion

        /// <summary>Called when an owner is destroyed: drop its waiters; cancel downloads nobody needs.</summary>
        private void RemoveWaitersOf(TextureRefTracker tracker)
        {
            List<string> emptyUrls = null;
            foreach (var kvp in _downloading)
            {
                var request = kvp.Value;
                request.Waiters.RemoveAll(w => w.HasOwner && ReferenceEquals(w.Tracker, tracker));
                if (request.Waiters.Count == 0 && !request.Cancelled)
                    (emptyUrls ??= new List<string>()).Add(kvp.Key);
            }

            if (emptyUrls == null) return;
            foreach (var url in emptyUrls)
                if (_downloading.TryGetValue(url, out var request) && request.Waiters.Count == 0 && !request.Cancelled)
                {
                    request.Cancelled = true;
                    if (request.Coroutine == null) _downloading.Remove(url);
                }
        }

        /// <summary>
        /// Normalize a URL so equivalent addresses share one cache/download slot. Query sorting is
        /// opt-in because it corrupts signed URLs.
        /// </summary>
        private string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;

            url = url.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

            var builder = new UriBuilder(uri)
            {
                Scheme = uri.Scheme.ToLowerInvariant(),
                Host = uri.Host.ToLowerInvariant(),
                UserName = string.Empty,
                Password = string.Empty,
                Fragment = string.Empty
            };

            var isDefaultPort = (builder.Scheme == "http" && builder.Port == 80) ||
                                (builder.Scheme == "https" && builder.Port == 443);
            if (isDefaultPort) builder.Port = -1;

            if (builder.Path.Length > 1 && builder.Path.EndsWith("/"))
                builder.Path = builder.Path.TrimEnd('/');

            if (sortQueryParameters && !string.IsNullOrEmpty(builder.Query))
            {
                var pairs = builder.Query.TrimStart('?').Split('&');
                Array.Sort(pairs, StringComparer.Ordinal);
                builder.Query = string.Join("&", pairs);
            }

            return builder.Uri.AbsoluteUri;
        }

        protected override void OnCleanup()
        {
            CancelAll();
            ClearCache();
            StopAllCoroutines();
        }

        #region Internal types

        /// <summary>A cached texture plus lazily created sprites keyed by pixelsPerUnit.</summary>
        private class CacheEntry
        {
            public Texture2D Texture;
            public LinkedListNode<string> LruNode;

            private readonly Dictionary<float, Sprite> _sprites = new Dictionary<float, Sprite>();

            public Sprite GetOrCreateSprite(float pixelsPerUnit)
            {
                if (Texture == null) return null;
                if (_sprites.TryGetValue(pixelsPerUnit, out var sprite) && sprite != null) return sprite;

                sprite = Sprite.Create(
                    Texture,
                    new Rect(0, 0, Texture.width, Texture.height),
                    new Vector2(0.5f, 0.5f),
                    pixelsPerUnit,
                    0,
                    SpriteMeshType.FullRect);
                _sprites[pixelsPerUnit] = sprite;
                return sprite;
            }

            public void Dispose()
            {
                foreach (var sprite in _sprites.Values)
                    if (sprite != null)
                        UnityEngine.Object.Destroy(sprite);
                _sprites.Clear();

                if (Texture != null) UnityEngine.Object.Destroy(Texture);
                Texture = null;
            }
        }

        /// <summary>One consumer waiting on a URL. With a tracker bound, the callback is dropped if the owner dies.</summary>
        private class Waiter
        {
            public TextureRefTracker Tracker;
            public bool HasOwner;
            public Action<CacheEntry> OnResolved;
        }

        private class DownloadRequest
        {
            public Coroutine Coroutine;
            public readonly List<Waiter> Waiters = new List<Waiter>();
            public bool Cancelled;
        }

        /// <summary>Per-owner lifetime tracker: releases refs and removes waiters when the owner is destroyed.</summary>
        private class TextureRefTracker : MonoBehaviour
        {
            private readonly Dictionary<string, int> _holds = new Dictionary<string, int>(StringComparer.Ordinal);
            private ImageDownloader _downloader;

            private void Awake()
            {
                hideFlags = HideFlags.HideAndDontSave;
            }

            public void Bind(ImageDownloader downloader) => _downloader = downloader;

            public void Hold(string url)
            {
                _holds.TryGetValue(url, out var count);
                _holds[url] = count + 1;
            }

            private void OnDestroy()
            {
                if (_downloader != null)
                {
                    foreach (var kvp in _holds)
                        for (var i = 0; i < kvp.Value; i++)
                            _downloader.ReleaseCacheRef(kvp.Key);

                    _downloader.RemoveWaitersOf(this);
                }

                _holds.Clear();
            }
        }

        #endregion
    }
}
