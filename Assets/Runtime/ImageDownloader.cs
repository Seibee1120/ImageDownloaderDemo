using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace ImageDownloaderDemo
{
    /// <summary>
    ///     Unity 的异步远程图片下载器。
    ///     主要特性:
    ///     - 请求去重：对同一 URL 的并发请求共享单次下载。
    ///     - LRU 纹理缓存，同时受数量上限和近似内存预算限制。
    ///     - 引用计数防止仍在使用的纹理被逐出。
    ///     - 所有者生命周期绑定：当发起请求的组件被销毁时，其回调会被丢弃，其缓存引用会自动释放。
    ///     - 有界并发，待处理队列、超时、重试和取消。
    /// </summary>
    public class ImageDownloader : MonoSingleton<ImageDownloader>
    {
        [Header("下载")] [Tooltip("每条请求的等待时间（秒）。<= 0 时禁用手动超时，可能导致请求永远挂起")]
        public float timeoutSeconds = 30f;

        [Tooltip("首次请求失败后的重试次数")] public int maxRetry = 2;

        [Tooltip("重试间隔")] public float retryDelaySeconds = 0.5f;

        [Tooltip("最大并发下载数，最小值为 1")] public int maxConcurrentDownloads = 8;

        [Header("缓存")] [Tooltip("缓存的纹理数量上限，<= 0 时无上限")]
        public int maxCacheCount = 50;

        [Tooltip("缓存的纹理内存上限（估值）， <= 0 时无内存上限")]
        public long maxCacheMemoryBytes = 100L * 1024 * 1024;

        [Tooltip("是否对查询参数排序（对于签名 URL 请保持关闭）")] public bool sortQueryParameters;

        // key => 归一化后的 URL，value => 缓存的纹理
        private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

        // key => 归一化后的 URL，value => 下载中/队列中的请求
        private readonly Dictionary<string, DownloadRequest> _downloading = new(StringComparer.Ordinal);

        // LRU 双向链表，最常用的在头部，最久未使用的在尾部
        private readonly LinkedList<string> _lruList = new();

        // key => 归一化后的 URL，value => 未释放的缓存引用总数
        private readonly Dictionary<string, int> _refCounts = new(StringComparer.Ordinal);

        private readonly Queue<string> _waitQueue = new();

        protected override void OnCleanup()
        {
            CancelAll();
            ClearCache();
            StopAllCoroutines();
        }

        /// <summary>
        ///     引用者被销毁时调用该方法，从请求的等待者列表中将其移除，如果该请求因此没有等待者，移除该请求
        /// </summary>
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
        ///     归一化处理 URL
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

        #region Public API

        /// <summary>
        ///     Asynchronously loads a texture.
        ///     A cache hit invokes <paramref name="onComplete" /> synchronously.
        /// </summary>
        public void GetTexture(string url, Component owner, Action<Texture2D> onComplete, bool forceRefresh = false)
        {
            Request(url, owner, entry => onComplete?.Invoke(entry?.Texture), forceRefresh);
        }

        /// <summary>
        ///     Asynchronously loads a texture without lifetime binding.
        ///     Callers must pair <see cref="RetainCacheRef" /> / <see cref="ReleaseCacheRef" /> themselves
        /// </summary>
        public void GetTexture(string url, Action<Texture2D> onComplete, bool forceRefresh = false)
        {
            Request(url, null, entry => onComplete?.Invoke(entry?.Texture), forceRefresh);
        }

        /// <summary>
        ///     Asynchronously loads a sprite (texture + Sprite cached per pixelsPerUnit).
        /// </summary>
        public void GetSprite(string url, Component owner, Action<Sprite> onComplete,
            float pixelsPerUnit = 100f, bool forceRefresh = false)
        {
            Request(url, owner, entry => onComplete?.Invoke(entry?.GetOrCreateSprite(pixelsPerUnit)), forceRefresh);
        }

        /// <summary>
        ///     Asynchronously loads a sprite without lifetime binding.
        ///     Callers must pair <see cref="RetainCacheRef" /> / <see cref="ReleaseCacheRef" /> themselves
        /// </summary>
        public void GetSprite(string url, Action<Sprite> onComplete,
            float pixelsPerUnit = 100f, bool forceRefresh = false)
        {
            Request(url, null, entry => onComplete?.Invoke(entry?.GetOrCreateSprite(pixelsPerUnit)), forceRefresh);
        }

        /// <summary>
        ///     Cancel one URL.
        ///     All waiters are invoked immediately with null.
        ///     Cached URLs are unaffected.
        /// </summary>
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

        /// <summary>
        ///     取消所有下载中和队列中的请求
        /// </summary>
        public void CancelAll()
        {
            var urls = new List<string>(_downloading.Keys);
            foreach (var url in urls) Cancel(url);
        }

        /// <summary>
        ///     Destroy and remove a single cached texture/sprite.
        /// </summary>
        public void RemoveFromCache(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            url = NormalizeUrl(url);
            if (_cache.TryGetValue(url, out var entry)) RemoveFromCache(url, entry);
        }

        /// <summary>
        ///     销毁所有缓存
        /// </summary>
        public void ClearCache()
        {
            foreach (var kvp in _cache) kvp.Value.Dispose();
            _cache.Clear();
            _lruList.Clear();
            _refCounts.Clear();
            CacheMemoryBytes = 0;
        }

        /// <summary>
        ///     Increase the cache reference count for a URL.
        ///     Pair with <see cref="ReleaseCacheRef" />.
        /// </summary>
        public void RetainCacheRef(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            url = NormalizeUrl(url);
            _refCounts.TryGetValue(url, out var count);
            _refCounts[url] = count + 1;
        }

        /// <summary>
        ///     Decrease the cache reference count. At zero the entry becomes evictable again.
        ///     Pair with <see cref="RetainCacheRef" />.
        /// </summary>
        public void ReleaseCacheRef(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            url = NormalizeUrl(url);
            if (!_refCounts.TryGetValue(url, out var count)) return;

            if (count <= 1) _refCounts.Remove(url);
            else _refCounts[url] = count - 1;

            EvictIfNeeded();
        }

        /// <summary>
        ///     Number of cached textures.
        /// </summary>
        public int CacheCount => _cache.Count;

        /// <summary>
        ///     Approximate cache memory usage in bytes.
        /// </summary>
        public long CacheMemoryBytes { get; private set; }

        /// <summary>
        ///     获取 URL 的引用计数
        /// </summary>
        public int GetCacheRefCount(string url)
        {
            if (string.IsNullOrEmpty(url)) return 0;
            return _refCounts.TryGetValue(NormalizeUrl(url), out var count) ? count : 0;
        }

        /// <summary>
        ///     Number of URLs that are downloading or queued.
        /// </summary>
        public int DownloadingCount => _downloading.Count;

        /// <summary>
        ///     Number of downloads actually transferring data right now.
        /// </summary>
        public int ActiveDownloadCount { get; private set; }

        #region 演示用计数器

        /// <summary>
        ///     正在等待并发槽位的请求数量
        /// </summary>
        public int QueuedCount => _waitQueue.Count;

        /// <summary>
        ///     Current waiting queue in FIFO order (head first), normalized URLs.
        ///     Cancelled entries that have not been pumped yet are skipped.
        /// </summary>
        public string[] GetQueuedUrls()
        {
            var result = new List<string>(_waitQueue.Count);
            foreach (var url in _waitQueue)
                if (_downloading.TryGetValue(url, out var request) && !request.Cancelled && request.Coroutine == null)
                    result.Add(url);
            return result.ToArray();
        }

        /// <summary>
        ///     真正开始下载时触发
        /// </summary>
        public event Action<string> DownloadStarted;

        /// <summary>
        ///     总请求数（包括缓存命中的情况）
        /// </summary>
        public int TotalRequests { get; private set; }

        /// <summary>
        ///     缓存命中次数
        /// </summary>
        public int TotalCacheHits { get; private set; }

        /// <summary>
        ///     被合并的请求数
        /// </summary>
        public int TotalMergedRequests { get; private set; }

        /// <summary>
        ///     真正下载的请求数
        /// </summary>
        public int TotalDownloadsStarted { get; private set; }

        /// <summary>
        ///     重置诊断计数器
        /// </summary>
        public void ResetStats()
        {
            TotalRequests = 0;
            TotalCacheHits = 0;
            TotalMergedRequests = 0;
            TotalDownloadsStarted = 0;
        }

        #endregion

        /// <summary>
        ///     Whether a URL is currently cached.
        /// </summary>
        public bool IsCached(string url)
        {
            return !string.IsNullOrEmpty(url) && _cache.ContainsKey(NormalizeUrl(url));
        }

        #endregion

        #region Scheduling

        /// <summary>
        ///     Single entry point: bind owner -> cache hit? -> de-duplicate/queue download.
        /// </summary>
        private void Request(string url, Component owner, Action<CacheEntry> onResolved, bool forceRefresh)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                Debug.LogWarning("[ImageDownloader] URL is null or empty.");
                InvokeWaiter(new Waiter { OnResolved = onResolved }, null);
                return;
            }

            url = NormalizeUrl(url);
            TotalRequests++;

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
                TotalCacheHits++;
                UpdateLruAccess(entry);
                InvokeWaiter(waiter, entry);
                return;
            }

            // 3. Force refresh -> drop the stale entry first.
            if (forceRefresh && _cache.TryGetValue(url, out var stale)) RemoveFromCache(url, stale);

            // 4. Merge into an existing request, otherwise create one.
            if (_downloading.TryGetValue(url, out var existing) && !existing.Cancelled)
            {
                TotalMergedRequests++;
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
            TotalDownloadsStarted++;

            // 先发布“拿到槽位”的事件，保证观察者看到的启动顺序与调度顺序一致；
            // 同时隔离订阅者异常，避免诊断代码影响真正的下载。
            try
            {
                DownloadStarted?.Invoke(url);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ImageDownloader] DownloadStarted handler threw: {e}");
            }

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

        /// <summary>
        ///     Common completion path: remove the request and invoke every waiter.
        /// </summary>
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

            // Owner already destroyed -> drop the callback.
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
                Destroy(texture);
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
            CacheMemoryBytes += EstimateTextureBytes(texture);
            EvictIfNeeded(url);
            return entry;
        }

        /// <summary>
        ///     Evict unreferenced cache entries if needed.
        /// </summary>
        /// <param name="protectUrl"></param>
        private void EvictIfNeeded(string protectUrl = null)
        {
            while (_lruList.Count > 0)
            {
                var overCount = maxCacheCount > 0 && _cache.Count > maxCacheCount;
                var overMemory = maxCacheMemoryBytes > 0 && CacheMemoryBytes > maxCacheMemoryBytes;
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
                CacheMemoryBytes -= EstimateTextureBytes(entry.Texture);

            entry.Dispose();

            if (entry.LruNode?.List != null) _lruList.Remove(entry.LruNode);
            _cache.Remove(url);
        }

        /// <summary>
        ///     Approximate texture memory in bytes.
        ///     Uses the real texture format and just-adds the mip chain (~1/3) for mipmapped textures.
        ///     This is an estimate, not a precise allocation size.
        /// </summary>
        private static long EstimateTextureBytes(Texture2D texture)
        {
            if (texture == null) return 0;

            var bitsPerPixel = BitsPerPixel(texture.format);
            var baseBytes = (long)texture.width * texture.height * bitsPerPixel / 8;
            if (baseBytes <= 0) return 0;

            var mipCount = Mathf.Max(1, texture.mipmapCount);
            return mipCount > 1 ? baseBytes * 4 / 3 : baseBytes;
        }

        private static int BitsPerPixel(TextureFormat format)
        {
            return format switch
            {
                TextureFormat.Alpha8 or TextureFormat.R8 => 8,
                TextureFormat.RGB565 or TextureFormat.ARGB4444 or TextureFormat.RGBA4444 => 16,
                TextureFormat.RGB24 => 24,
                TextureFormat.RGBAHalf => 64,
                TextureFormat.RGBAFloat => 128,

                // RGBA32 / ARGB32 / BGRA32 and everything else.
                _ => 32
            };
        }

        private void UpdateLruAccess(CacheEntry entry)
        {
            if (entry.LruNode?.List == null || ReferenceEquals(_lruList.First, entry.LruNode)) return;
            _lruList.Remove(entry.LruNode);
            _lruList.AddFirst(entry.LruNode);
        }

        #endregion

        #region Internal types

        /// <summary>
        ///     A cached texture plus lazily created sprites keyed by pixelsPerUnit.
        /// </summary>
        private class CacheEntry
        {
            private readonly Dictionary<float, Sprite> _sprites = new();
            public LinkedListNode<string> LruNode;
            public Texture2D Texture;

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
                        Destroy(sprite);
                _sprites.Clear();

                if (Texture != null) Destroy(Texture);
                Texture = null;
            }
        }

        /// <summary>
        ///     One consumer waiting on a URL. With a tracker bound, the callback is dropped if the owner dies.
        /// </summary>
        private class Waiter
        {
            public bool HasOwner;
            public Action<CacheEntry> OnResolved;
            public TextureRefTracker Tracker;
        }

        private class DownloadRequest
        {
            public readonly List<Waiter> Waiters = new();
            public bool Cancelled;
            public Coroutine Coroutine;
        }

        /// <summary>
        ///     Per-owner lifetime tracker: releases refs and removes waiters when the owner is destroyed.
        /// </summary>
        private class TextureRefTracker : MonoBehaviour
        {
            private readonly Dictionary<string, int> _holds = new(StringComparer.Ordinal);
            private ImageDownloader _downloader;

            private void Awake()
            {
                hideFlags = HideFlags.HideAndDontSave;
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

            public void Bind(ImageDownloader downloader)
            {
                _downloader = downloader;
            }

            public void Hold(string url)
            {
                _holds.TryGetValue(url, out var count);
                _holds[url] = count + 1;
            }
        }

        #endregion
    }
}