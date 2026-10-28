using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ImageDownloaderDemo.Demo.ConcurrencyQueueDemo
{
    /// <summary>
    ///     并发上限 / FIFO 等待队列 Demo。
    ///     每批在同一帧发起 N 个互不相同的 URL：
    ///     - 前 maxConcurrentDownloads 个立即获得槽位并开始传输；
    ///     - 其余请求按发起顺序进入等待队列，槽位释放时从队首依次启动；
    ///     - 记录每个请求真正的启动时间（ImageDownloader.DownloadStarted）与队列快照，
    ///       用启动顺序直接验证 FIFO，而不是只看“完成了几个”。
    /// </summary>
    [AddComponentMenu("ImageDownloader Demo/Concurrency Queue Demo")]
    public class ConcurrencyQueueDemo : MonoBehaviour
    {
        private static GUIStyle _titleStyle;
        private static GUIStyle _wrapStyle;
        private static GUIStyle _lineStyle;

        [Header("并发")] [Tooltip("演示期间临时覆盖 ImageDownloader.maxConcurrentDownloads，退出 Demo 时恢复原值。")]
        [Range(1, 12)]
        [SerializeField]
        private int concurrentLimit = 3;

        [Header("请求")] [Tooltip("每次 Fire 发起的唯一 URL 数量。大于并发上限时，多出来的请求会进入 FIFO 等待队列。")]
        [Range(1, 32)]
        [SerializeField]
        private int requestCount = 20;

        [Tooltip("图片边长（像素）。越大单次传输越久，越容易看清并发波形。")]
        [Range(256, 1024)]
        [SerializeField]
        private int imageSize = 768;

        private readonly List<RequestRecord> _records = new();
        private readonly Dictionary<string, RequestRecord> _recordsByKey = new(StringComparer.Ordinal);
        private readonly List<int> _startOrder = new();

        private bool _cancelled;
        private float _lastFinishedAt;
        private int _maxActive;
        private int _maxQueued;
        private int _originalConcurrency;
        private bool _overLimit;
        private Vector2 _pageScroll;
        private string[] _queuedUrls = Array.Empty<string>();
        private bool _restoreConcurrency;
        private RunState _run;
        private int _runId;
        private int _seedSerial;
        private bool _subscribed;
        private Vector2 _timelineScroll;

        private bool RunActive => _run != null && _run.Completed < _run.Issued;

        private static GUIStyle TitleStyle
        {
            get
            {
                if (_titleStyle != null) return _titleStyle;
                _titleStyle = new GUIStyle(GUI.skin.label)
                {
                    richText = true,
                    fontSize = 16,
                    fontStyle = FontStyle.Bold
                };
                return _titleStyle;
            }
        }

        private static GUIStyle WrapStyle
        {
            get
            {
                if (_wrapStyle != null) return _wrapStyle;
                _wrapStyle = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
                return _wrapStyle;
            }
        }

        private static GUIStyle LineStyle
        {
            get
            {
                if (_lineStyle != null) return _lineStyle;
                _lineStyle = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, fontSize = 11 };
                return _lineStyle;
            }
        }

        private void OnEnable()
        {
            ImageDownloader.Instance.DownloadStarted += HandleDownloadStarted;
            _subscribed = true;
        }

        private void OnDisable()
        {
            if (_subscribed && ImageDownloader.HasInstance)
                ImageDownloader.Instance.DownloadStarted -= HandleDownloadStarted;
            _subscribed = false;
        }

        private void Start()
        {
            var downloader = ImageDownloader.Instance;
            _originalConcurrency = downloader.maxConcurrentDownloads;
            _restoreConcurrency = true;
            downloader.maxConcurrentDownloads = Mathf.Max(1, concurrentLimit);
        }

        private void OnDestroy()
        {
            if (_restoreConcurrency && ImageDownloader.HasInstance)
                ImageDownloader.Instance.maxConcurrentDownloads = _originalConcurrency;
        }

        private void Update()
        {
            if (!ImageDownloader.HasInstance) return;
            var downloader = ImageDownloader.Instance;

            if (_run == null)
            {
                if (_queuedUrls.Length > 0) _queuedUrls = Array.Empty<string>();
                return;
            }

            _queuedUrls = downloader.GetQueuedUrls();

            // 只统计本批请求的并发数，避免上一批尚未收尾的协程造成误判。
            var activeInRun = 0;
            foreach (var record in _records)
                if (record.StartedAt >= 0 && !record.Completed)
                    activeInRun++;

            _maxActive = Mathf.Max(_maxActive, activeInRun);
            _maxQueued = Mathf.Max(_maxQueued, _queuedUrls.Length);
            if (activeInRun > _run.Limit) _overLimit = true;

            RefreshQueuePositions();
        }

        private void RefreshQueuePositions()
        {
            foreach (var record in _records)
                if (!record.Completed && record.StartedAt < 0)
                    record.QueuePosition = 0;

            for (var i = 0; i < _queuedUrls.Length; i++)
                if (_recordsByKey.TryGetValue(_queuedUrls[i], out var record) && record.StartedAt < 0)
                    record.QueuePosition = i + 1;
        }

        private void OnGUI()
        {
            var downloader = ImageDownloader.HasInstance ? ImageDownloader.Instance : null;
            var width = Mathf.Min(Screen.width - 24f, 1040f);

            _pageScroll = GUILayout.BeginScrollView(_pageScroll, GUILayout.Width(width),
                GUILayout.Height(Screen.height - 8f));
            GUILayout.BeginVertical(GUILayout.Width(width - 20f));

            GUILayout.Label("<b>Concurrency Limit / FIFO Queue Demo</b>", TitleStyle);
            GUILayout.Label(
                "ImageDownloader 同时最多跑 <b>maxConcurrentDownloads</b> 条传输；放不下的请求按<b>发起顺序</b>" +
                "进入等待队列，槽位一释放就从<b>队首</b>取出。每批请求都在同一帧发起 N 个互不相同的 URL，" +
                "并通过 <b>DownloadStarted</b> 事件记录每次“真正拿到槽位”的时间。\n" +
                "预期：任意时刻 <b>active ≤ limit</b>；启动顺序严格等于 <b>#00 → #01 → … → #N-1</b>。" +
                "完成顺序可以不同（图片大小/网络耗时不同）——FIFO 约束的是<b>开始</b>，不是结束。",
                WrapStyle);

            GUILayout.Space(6);
            DrawControls(downloader);
            GUILayout.Space(6);
            DrawLiveState(downloader);

            if (_run != null)
            {
                GUILayout.Space(6);
                DrawQueue();
                GUILayout.Space(6);
                DrawTimeline();
                GUILayout.Space(6);
                DrawVerdict();
            }

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        private void DrawControls(ImageDownloader downloader)
        {
            var editable = !RunActive;

            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUI.enabled = editable;
            GUILayout.Label($"Max concurrent: {concurrentLimit}", GUILayout.Width(150));
            var newLimit = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(concurrentLimit, 1f, 12f, GUILayout.Width(160)));
            GUI.enabled = true;
            if (newLimit != concurrentLimit)
            {
                concurrentLimit = newLimit;
                if (downloader != null && !RunActive)
                    downloader.maxConcurrentDownloads = Mathf.Max(1, concurrentLimit);
            }

            GUILayout.Space(18);
            GUI.enabled = editable;
            GUILayout.Label($"Requests: {requestCount}", GUILayout.Width(100));
            requestCount = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(requestCount, 1f, 32f, GUILayout.Width(160)));
            GUI.enabled = true;

            GUILayout.Space(18);
            GUI.enabled = editable;
            GUILayout.Label($"Size: {imageSize}px", GUILayout.Width(92));
            imageSize = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(imageSize, 256f, 1024f, GUILayout.Width(140)));
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = !RunActive;
            if (GUILayout.Button($"Fire {requestCount} unique URLs", GUILayout.Height(30))) Fire();
            GUI.enabled = RunActive;
            if (GUILayout.Button("Cancel all", GUILayout.Height(30))) CancelRun();
            GUI.enabled = true;
            if (GUILayout.Button("Clear cache", GUILayout.Height(30)))
                downloader?.ClearCache();
            if (GUILayout.Button("Reset stats", GUILayout.Height(30)))
                downloader?.ResetStats();
            GUILayout.EndHorizontal();

            if (RunActive)
                GUILayout.Label(
                    $"传输中… 已完成 {_run.Completed}/{_run.Issued}。为避免改变本次实验的条件，参数在批次结束前锁定。",
                    WrapStyle);
            else if (_run != null)
                GUILayout.Label(
                    "✔ 本批已结束。看下方 <b>start order</b> 与 verdict；再次 Fire 会换一组全新的 URL。", WrapStyle);
            else
                GUILayout.Label(
                    "把 <b>Requests</b> 调到大于 <b>Max concurrent</b>，再点 Fire：前 N 个立即开始，其余排队。" +
                    "队列区会实时显示队首顺序，时间线用灰色表示排队、绿色表示传输。", WrapStyle);

            GUILayout.EndVertical();
        }

        private void DrawLiveState(ImageDownloader downloader)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<b>Live concurrency</b>", WrapStyle);

            if (downloader == null)
            {
                GUILayout.Label("ImageDownloader 尚未创建；点 Fire 会自动创建单例。", LineStyle);
                GUILayout.EndVertical();
                return;
            }

            var limit = Mathf.Max(1, downloader.maxConcurrentDownloads);
            var active = downloader.ActiveDownloadCount;
            var queued = downloader.GetQueuedUrls().Length;

            GUILayout.Label(
                $"slots {SlotBar(active, limit)}    active <b>{active}</b>/{limit}    " +
                $"queued <b>{queued}</b>    tracked {downloader.DownloadingCount}", WrapStyle);

            if (_run != null)
                GUILayout.Label(
                    $"run {_run.Completed}/{_run.Issued} completed, {_run.Failed} failed    " +
                    $"peak active {_maxActive}    peak queue {_maxQueued}" +
                    (_overLimit ? "    <color=#ff7777><b>limit exceeded!</b></color>" : string.Empty),
                    WrapStyle);
            else
                GUILayout.Label("run —（尚未发起批次）", WrapStyle);

            GUILayout.Label(
                $"session: requests={downloader.TotalRequests}  starts={downloader.TotalDownloadsStarted}  " +
                $"cacheHits={downloader.TotalCacheHits}  merged={downloader.TotalMergedRequests}  " +
                $"cache={downloader.CacheCount}/{Mathf.Max(0, downloader.maxCacheCount)}", WrapStyle);

            GUILayout.EndVertical();
        }

        private void DrawQueue()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label($"<b>FIFO wait queue</b> — head at top ({_queuedUrls.Length} waiting)", WrapStyle);

            if (_queuedUrls.Length == 0)
            {
                GUILayout.Label("（空：所有请求都已拿到槽位）", LineStyle);
            }
            else
            {
                var shown = Mathf.Min(_queuedUrls.Length, 12);
                for (var i = 0; i < shown; i++)
                {
                    var url = _queuedUrls[i];
                    if (_recordsByKey.TryGetValue(url, out var record))
                    {
                        var wait = Mathf.Max(0f, Time.realtimeSinceStartup - record.IssuedAt);
                        GUILayout.Label(
                            $"  {i + 1,2}. #{record.Index:00}  seed={record.Seed}  waiting {wait:F2}s",
                            LineStyle);
                    }
                    else
                    {
                        GUILayout.Label($"  {i + 1,2}. {url}", LineStyle);
                    }
                }

                if (_queuedUrls.Length > shown)
                    GUILayout.Label($"  … {_queuedUrls.Length - shown} more", LineStyle);
            }

            GUILayout.EndVertical();
        }

        private void DrawTimeline()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(
                "<b>Request timeline</b>  — grey = waiting in queue, green = transfer, red = failed",
                WrapStyle);

            var now = Time.realtimeSinceStartup;
            var end = _run.Completed >= _run.Issued && _lastFinishedAt > 0f ? _lastFinishedAt : now;
            var span = Mathf.Max(0.5f, end - _run.FiredAt);

            _timelineScroll = GUILayout.BeginScrollView(_timelineScroll,
                GUILayout.Height(Mathf.Min(280f, _records.Count * 17f + 8f)));

            foreach (var record in _records)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"#{record.Index:00}", LineStyle, GUILayout.Width(32));
                var rect = GUILayoutUtility.GetRect(240f, 14f, GUILayout.ExpandWidth(true));
                DrawTimelineRow(rect, record, _run.FiredAt, now, span);
                GUILayout.Label(StatusOf(record, now), LineStyle, GUILayout.Width(230));
                GUILayout.EndHorizontal();
            }

            GUILayout.EndScrollView();
            GUILayout.Label($"span ≈ {span:F2}s（第一个请求的发起时刻为 t=0）", LineStyle);
            GUILayout.EndVertical();
        }

        private void DrawVerdict()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<b>FIFO evidence</b>", WrapStyle);
            GUILayout.Label(
                "start order (slot acquisition): " +
                (_startOrder.Count == 0 ? "—" : FormatStartOrder()),
                WrapStyle);

            var verdict = BuildVerdict();
            GUILayout.Label(verdict ?? $"still running… {_run.Completed}/{_run.Issued}", WrapStyle);

            GUILayout.EndVertical();
        }

        private void Fire()
        {
            if (RunActive || !ImageDownloader.HasInstance) return;
            var downloader = ImageDownloader.Instance;
            downloader.maxConcurrentDownloads = Mathf.Max(1, concurrentLimit);

            _runId++;
            var runId = _runId;
            _seedSerial++;

            _records.Clear();
            _recordsByKey.Clear();
            _startOrder.Clear();
            _queuedUrls = Array.Empty<string>();
            _maxActive = 0;
            _maxQueued = 0;
            _overLimit = false;
            _cancelled = false;
            _lastFinishedAt = -1f;

            var firedAt = Time.realtimeSinceStartup;
            var seedBase = _seedSerial * 1000;

            // 先把本批记录建好，再发请求：DownloadStarted 可能在 GetTexture 内同步触发。
            for (var i = 0; i < requestCount; i++)
            {
                var seed = seedBase + i;
                var url = $"https://picsum.photos/seed/concurrency{seed}/{imageSize}";
                var record = new RequestRecord
                {
                    Index = i,
                    Seed = seed,
                    Url = url,
                    Key = NormalizeKey(url),
                    IssuedAt = firedAt
                };
                _records.Add(record);
                _recordsByKey[record.Key] = record;
            }

            _run = new RunState
            {
                Issued = requestCount,
                Limit = Mathf.Max(1, concurrentLimit),
                FiredAt = firedAt
            };

            foreach (var record in _records)
            {
                var captured = record;
                // forceRefresh：本 Demo 只关心调度，确保每个 URL 都产生一次真实传输而不是缓存命中。
                downloader.GetTexture(captured.Url, texture => OnComplete(runId, captured, texture), true);
            }

            _queuedUrls = downloader.GetQueuedUrls();
            _maxQueued = Mathf.Max(_maxQueued, _queuedUrls.Length);

            var startedNow = 0;
            foreach (var record in _records)
                if (record.StartedAt >= 0 && !record.Completed)
                    startedNow++;
            _maxActive = Mathf.Max(_maxActive, startedNow);
            if (startedNow > _run.Limit) _overLimit = true;

            RefreshQueuePositions();
        }

        private void CancelRun()
        {
            if (!ImageDownloader.HasInstance) return;
            _cancelled = true;
            ImageDownloader.Instance.CancelAll();
            _queuedUrls = Array.Empty<string>();
        }

        private void HandleDownloadStarted(string normalizedUrl)
        {
            if (_run == null || !_recordsByKey.TryGetValue(normalizedUrl, out var record)) return;
            if (record.StartedAt >= 0) return;

            record.StartedAt = Time.realtimeSinceStartup;
            record.QueuePosition = 0;
            _startOrder.Add(record.Index);
        }

        private void OnComplete(int runId, RequestRecord record, Texture2D texture)
        {
            if (runId != _runId || _run == null || record.Completed) return;

            record.Completed = true;
            record.Failed = texture == null;
            record.FinishedAt = Time.realtimeSinceStartup;
            if (record.StartedAt < 0) record.StartedAt = record.FinishedAt;
            record.QueuePosition = 0;

            _run.Completed++;
            if (record.Failed) _run.Failed++;
            _lastFinishedAt = Mathf.Max(_lastFinishedAt, record.FinishedAt);
        }

        private string BuildVerdict()
        {
            if (_run == null || _run.Completed < _run.Issued) return null;

            var startsInIssueOrder = _startOrder.Count == _records.Count;
            var firstBreak = -1;
            for (var i = 1; i < _startOrder.Count; i++)
                if (_startOrder[i] <= _startOrder[i - 1])
                {
                    startsInIssueOrder = false;
                    firstBreak = i;
                    break;
                }

            var failNote = _run.Failed > 0 ? $"，其中 {_run.Failed} 个失败/被取消" : string.Empty;

            if (_cancelled)
                return $"已取消：{_run.Completed}/{_run.Issued} 个请求结束（{_run.Failed} 个回调为 null）。";

            if (_overLimit || _maxActive > _run.Limit)
                return $"✘ LIMIT BROKEN：观察到 <b>{_maxActive}</b> 个并发传输，超过上限 {_run.Limit}。";

            if (!startsInIssueOrder)
                return $"✘ FIFO BROKEN：实际启动顺序与发起顺序不一致（第 {firstBreak} 个启动项开始乱序）。";

            if (_maxQueued == 0)
                return $"✔ BOUND：峰值并发 {_maxActive}/{_run.Limit}。本批没有形成等待队列" +
                       $"（Requests ≤ 并发上限）；把 Requests 调大即可观察 FIFO{failNote}。";

            return $"✔ FIFO + BOUND：{_run.Issued} 个请求，峰值并发 {_maxActive}/{_run.Limit}，" +
                   $"峰值排队 {_maxQueued}；{_run.Issued} 次启动全部按发起顺序获得槽位{failNote}。";
        }

        private string FormatStartOrder()
        {
            var builder = new StringBuilder();
            for (var i = 0; i < _startOrder.Count; i++)
            {
                if (i > 0) builder.Append(" → ");
                builder.Append('#').Append(_startOrder[i].ToString("00"));
            }

            return builder.ToString();
        }

        private string StatusOf(RequestRecord record, float now)
        {
            if (record.Completed)
            {
                var wait = Mathf.Max(0f, record.StartedAt - record.IssuedAt);
                var run = Mathf.Max(0f, record.FinishedAt - record.StartedAt);
                return $"{(record.Failed ? "FAILED" : "done")}  wait {wait:F2}s + run {run:F2}s";
            }

            if (record.StartedAt >= 0)
                return $"running  {now - record.StartedAt:F2}s  (queued {record.StartedAt - record.IssuedAt:F2}s)";

            return $"queued #{record.QueuePosition}  {now - record.IssuedAt:F2}s";
        }

        private static string SlotBar(int active, int limit)
        {
            active = Mathf.Clamp(active, 0, limit);
            var builder = new StringBuilder("[");
            for (var i = 0; i < limit; i++) builder.Append(i < active ? '#' : '-');
            builder.Append(']');
            return builder.ToString();
        }

        private static void DrawTimelineRow(Rect rect, RequestRecord record, float origin, float now, float span)
        {
            var oldColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.08f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = oldColor;

            var queueEnd = record.StartedAt >= 0 ? record.StartedAt : now;
            if (queueEnd - record.IssuedAt > 0.02f)
                DrawSegment(rect, origin, record.IssuedAt, queueEnd, span,
                    new Color(0.95f, 0.72f, 0.25f, 0.75f));

            if (record.StartedAt < 0) return;

            var runEnd = record.Completed ? record.FinishedAt : now;
            DrawSegment(rect, origin, record.StartedAt, runEnd, span,
                record.Failed
                    ? new Color(1f, 0.35f, 0.35f, 0.9f)
                    : new Color(0.36f, 0.82f, 0.45f, 0.9f));
        }

        private static void DrawSegment(Rect rect, float origin, float from, float to, float span, Color color)
        {
            var x0 = Mathf.Clamp01((from - origin) / span);
            var x1 = Mathf.Clamp01((to - origin) / span);
            if (x1 < x0) x1 = x0;

            var startX = rect.x + x0 * rect.width;
            var endX = Mathf.Min(rect.x + x1 * rect.width, rect.xMax);
            var width = Mathf.Max(2f, endX - startX);
            width = Mathf.Min(width, rect.xMax - startX);
            if (width <= 0f) return;

            var oldColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(startX, rect.y + 3f, width, Mathf.Max(2f, rect.height - 6f)),
                Texture2D.whiteTexture);
            GUI.color = oldColor;
        }

        /// <summary>
        ///     与 ImageDownloader.NormalizeUrl 对齐的 key，用于把 DownloadStarted 的回调映射回请求记录。
        /// </summary>
        private static string NormalizeKey(string url)
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

            return builder.Uri.AbsoluteUri;
        }

        private sealed class RunState
        {
            public int Completed;
            public int Failed;
            public float FiredAt;
            public int Issued;
            public int Limit;
        }

        private sealed class RequestRecord
        {
            public bool Completed;
            public bool Failed;
            public float FinishedAt = -1f;
            public int Index;
            public float IssuedAt;
            public string Key;
            public int QueuePosition;
            public int Seed;
            public float StartedAt = -1f;
            public string Url;
        }
    }
}
