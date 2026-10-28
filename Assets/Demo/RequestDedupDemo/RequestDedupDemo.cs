using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace ImageDownloaderDemo.Demo.RequestDedupDemo
{
    /// <summary>
    ///     请求去重 Demo
    /// </summary>
    public class RequestDedupDemo : MonoBehaviour
    {
        private static GUIStyle _titleStyle;
        private static GUIStyle _wrapStyle;
        private static GUIStyle _lineStyle;

        [Header("请求")] [SerializeField] private string url = "https://picsum.photos/seed/dedup/512";

        [Range(1, 128)] [SerializeField] private int consumerCount = 24;

        [Tooltip("同一个 URL 的四种等价写法：原样、末尾斜杠、fragment、大写 scheme/host。用于同时观察 URL 归一化。")] [SerializeField]
        private bool useEquivalentUrlForms;

        [Tooltip("以本对象作为 owner 绑定生命周期（推荐用法）。关闭后使用无 owner 的 API。")] [SerializeField]
        private bool bindOwnerLifetime = true;

        private readonly HashSet<int> _textureIds = new();
        private int _baseDownloads;
        private int _baseHits;
        private int _baseMerged;

        private int _baseRequests;
        private ConsumerState[] _consumers = Array.Empty<ConsumerState>();
        private float _fireRealtime;
        private bool _firing;
        private Texture2D _preview;

        private RunSnapshot _run;

        private int _runId;
        private Vector2 _scroll;

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
                _lineStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
                return _lineStyle;
            }
        }

        private bool Pending => _run != null && _run.Completed < _run.Issued;

        private void OnGUI()
        {
            var downloader = ImageDownloader.HasInstance ? ImageDownloader.Instance : null;

            GUILayout.BeginVertical(GUILayout.Width(Mathf.Min(Screen.width - 24f, 840f)));

            GUILayout.Label("<b>Request De-duplication Demo</b>", TitleStyle);
            GUILayout.Label(
                "每次点击都会在 <b>同一帧</b> 内对同一个 URL 发起 <b>N</b> 个请求。" +
                "如果去重生效，下载器只会启动 <b>1</b> 次传输，把其余 <b>N-1</b> 个请求合并进去，" +
                "然后所有调用者拿到同一个纹理。下面的数字直接读取 ImageDownloader 的计数器" +
                "（TotalRequests / TotalDownloadsStarted / TotalMergedRequests / TotalCacheHits），" +
                "不是根据 URL 列表推算的。",
                WrapStyle);

            GUILayout.Space(6);
            DrawControls(downloader);
            GUILayout.Space(6);
            DrawRunResult();
            GUILayout.Space(6);
            DrawConsumers();

            GUILayout.EndVertical();
        }

        private void DrawControls(ImageDownloader downloader)
        {
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label("URL", GUILayout.Width(34));
            url = GUILayout.TextField(url ?? string.Empty);
            if (GUILayout.Button("New URL", GUILayout.Width(80)))
            {
                url = $"https://picsum.photos/seed/dedup{Random.Range(100000, 999999)}/512";
                ResetRun();
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Consumers", GUILayout.Width(68));
            consumerCount = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(consumerCount, 1, 128, GUILayout.Width(200)));
            GUILayout.Label(consumerCount.ToString(), GUILayout.Width(36));
            useEquivalentUrlForms = GUILayout.Toggle(useEquivalentUrlForms, "4 equivalent URL forms");
            bindOwnerLifetime = GUILayout.Toggle(bindOwnerLifetime, "bind owner lifetime");
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = !Pending && !string.IsNullOrWhiteSpace(url);
            if (GUILayout.Button($"Fire {consumerCount} concurrent", GUILayout.Height(30)))
                Fire(false);
            if (GUILayout.Button("Force refresh", GUILayout.Height(30)))
                Fire(true);
            GUI.enabled = true;

            if (GUILayout.Button("Clear cache", GUILayout.Width(100), GUILayout.Height(30)))
            {
                downloader?.ClearCache();
                ResetRun();
            }

            if (GUILayout.Button("Reset stats", GUILayout.Width(100), GUILayout.Height(30)))
            {
                downloader?.ResetStats();
                ResetRun();
            }

            GUILayout.EndHorizontal();

            if (Pending)
                GUILayout.Label($"等待回调… {_run.Completed}/{_run.Issued}");
            else
                GUILayout.Label(
                    "提示：第一次加载后直接再点 Fire 会走缓存（0 次下载、N 次 cache hit）；" +
                    "点 Force refresh 可以绕过缓存再次演示去重。",
                    WrapStyle);

            GUILayout.EndVertical();
        }

        private void DrawRunResult()
        {
            if (_run == null) return;
            var r = _run;

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<b>Last run</b>", WrapStyle);

            var state = r.Completed < r.Issued ? $"downloading… {r.Completed}/{r.Issued}" : "complete";
            GUILayout.Label($"state: {state}    elapsed: {r.ElapsedMs:F0} ms");

            GUILayout.Label($"requests issued          : {r.Requests}  (consumers={r.Issued})");
            GUILayout.Label($"downloads started        : {r.DownloadsStarted}  (dedup expects 1)");
            GUILayout.Label($"merged into in-flight    : {r.Merged}  (dedup expects N-1)");
            GUILayout.Label($"cache hits               : {r.CacheHits}  (cache expects N)");
            GUILayout.Label(
                $"callbacks (sync / async) : {r.Synchronous} / {Mathf.Max(0, r.Completed - r.Synchronous)}");
            GUILayout.Label($"distinct Texture2D       : {_textureIds.Count}  (dedup expects 1)");
            GUILayout.Label($"failed / cancelled       : {r.Failed}");
            GUILayout.Label(
                $"at fire                  : downloading={r.DownloadingAtFire}  active={r.ActiveAtFire}  queued={r.QueuedAtFire}");
            GUILayout.Label($"cache count (before→fire): {r.CacheCountBefore} → {r.CacheCountAfter}");

            var accounted = r.DownloadsStarted + r.Merged + r.CacheHits;
            GUILayout.Label(
                $"ledger                   : {r.DownloadsStarted} downloads + {r.Merged} merged + {r.CacheHits} cache hits = {accounted} / {r.Requests} requests");

            if (!string.IsNullOrEmpty(r.Verdict))
                GUILayout.Label($"<b>{r.Verdict}</b>", WrapStyle);

            if (_preview != null)
            {
                GUILayout.BeginHorizontal();
                var rect = GUILayoutUtility.GetRect(96, 96, GUILayout.Width(96), GUILayout.Height(96));
                GUI.DrawTexture(rect, _preview, ScaleMode.ScaleToFit);
                GUILayout.Label("所有成功回调收到的是同一个 Texture2D 实例。", WrapStyle);
                GUILayout.EndHorizontal();
            }

            GUILayout.EndVertical();
        }

        private void DrawConsumers()
        {
            if (_run == null) return;

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label($"<b>Consumers</b>  {_run.Completed}/{_run.Issued} callbacks", WrapStyle);

            var height = Mathf.Min(240f, 18f * _run.Issued + 6f);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(height));
            for (var i = 0; i < _run.Issued; i++)
            {
                var c = _consumers[i];
                string line;
                if (!c.Done)
                    line = $"#{i,3}  waiting…";
                else if (c.Failed)
                    line = $"#{i,3}  FAILED / cancelled";
                else
                    line = $"#{i,3}  ok  tex={c.TextureId}  frame={c.Frame}  +{c.ElapsedSeconds * 1000f:F0}ms  " +
                           (c.Synchronous ? "sync (cache)" : "async");
                GUILayout.Label(line, LineStyle);
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void Fire(bool forceRefresh)
        {
            var downloader = ImageDownloader.Instance;
            if (downloader == null || string.IsNullOrWhiteSpace(url)) return;

            // Old callbacks may still arrive after Clear/Reset; the run id invalidates them.
            _runId++;
            var runId = _runId;

            _consumers = new ConsumerState[consumerCount];
            _textureIds.Clear();
            _preview = null;

            _run = new RunSnapshot
            {
                Issued = consumerCount,
                CacheCountBefore = downloader.CacheCount
            };

            _baseRequests = downloader.TotalRequests;
            _baseDownloads = downloader.TotalDownloadsStarted;
            _baseMerged = downloader.TotalMergedRequests;
            _baseHits = downloader.TotalCacheHits;

            _firing = true;
            _fireRealtime = Time.realtimeSinceStartup;

            for (var i = 0; i < consumerCount; i++)
            {
                var requestUrl = useEquivalentUrlForms ? EquivalentUrlFor(i) : url.Trim();
                var index = i;
                Action<Texture2D> onComplete = texture => OnComplete(runId, index, texture);

                if (bindOwnerLifetime)
                    downloader.GetTexture(requestUrl, this, onComplete, forceRefresh);
                else
                    downloader.GetTexture(requestUrl, onComplete, forceRefresh);
            }

            _firing = false;

            _run.Requests = downloader.TotalRequests - _baseRequests;
            _run.DownloadsStarted = downloader.TotalDownloadsStarted - _baseDownloads;
            _run.Merged = downloader.TotalMergedRequests - _baseMerged;
            _run.CacheHits = downloader.TotalCacheHits - _baseHits;
            _run.DownloadingAtFire = downloader.DownloadingCount;
            _run.ActiveAtFire = downloader.ActiveDownloadCount;
            _run.QueuedAtFire = downloader.QueuedCount;
            _run.CacheCountAfter = downloader.CacheCount;
            _run.Synchronous = _run.Completed;

            if (_run.Completed == _run.Issued)
                _run.Verdict = BuildVerdict();
        }

        private void OnComplete(int runId, int index, Texture2D texture)
        {
            if (runId != _runId || _run == null || index < 0 || index >= _consumers.Length) return;
            if (_consumers[index].Done) return;

            var state = new ConsumerState
            {
                Done = true,
                Failed = texture == null,
                Synchronous = _firing,
                TextureId = texture != null ? texture.GetInstanceID() : 0,
                Frame = Time.frameCount,
                ElapsedSeconds = Time.realtimeSinceStartup - _fireRealtime
            };
            _consumers[index] = state;

            _run.Completed++;
            _run.ElapsedMs = Mathf.Max(_run.ElapsedMs, state.ElapsedSeconds * 1000f);

            if (texture == null)
            {
                _run.Failed++;
            }
            else
            {
                _textureIds.Add(state.TextureId);
                if (_preview == null) _preview = texture;
            }

            // Synchronous callbacks run inside Fire(), before the snapshot is filled in;
            // the verdict is built once at the end of Fire() for those.
            if (_run.Completed == _run.Issued && !_firing)
                _run.Verdict = BuildVerdict();
        }

        private string BuildVerdict()
        {
            var r = _run;
            var failNote = r.Failed > 0 ? $", {r.Failed} failed/cancelled" : string.Empty;

            if (r.DownloadsStarted == 1 && r.Merged == r.Issued - 1 && r.CacheHits == 0)
                return $"✔ DEDUP: {r.Issued} requests → 1 download, {r.Merged} merged, " +
                       $"{r.Completed} callbacks, {_textureIds.Count} distinct texture(s){failNote}.";

            if (r.DownloadsStarted == 0 && r.Merged == r.Issued && r.CacheHits == 0)
                return $"✔ MERGED: all {r.Issued} requests joined an already-running download " +
                       $"(0 new transfers){failNote}.";

            if (r.DownloadsStarted == 0 && r.CacheHits == r.Issued)
                return $"✔ CACHE HIT: {r.Issued} requests served synchronously by the cache, 0 downloads.";

            return $"Observed: requests={r.Requests}, downloads={r.DownloadsStarted}, " +
                   $"merged={r.Merged}, cacheHits={r.CacheHits}{failNote}.";
        }

        private void ResetRun()
        {
            _runId++; // ignore callbacks belonging to the old run
            _run = null;
            _consumers = Array.Empty<ConsumerState>();
            _textureIds.Clear();
            _preview = null;
        }

        /// <summary>
        ///     Produce four textually different but logically equivalent forms of the URL.
        ///     The downloader's normalizer should fold all four to the same cache/download key.
        /// </summary>
        private string EquivalentUrlFor(int index)
        {
            var raw = url.Trim();
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return raw;

            switch (index & 3)
            {
                case 1:
                    return raw.TrimEnd('/') + "/";
                case 2:
                    return raw + "#dedup-fragment";
                case 3:
                {
                    var withUpperAuthority = raw.Replace(
                        "://" + uri.Authority,
                        "://" + uri.Authority.ToUpperInvariant());
                    return withUpperAuthority.Replace(
                        uri.Scheme + "://",
                        uri.Scheme.ToUpperInvariant() + "://");
                }
                default:
                    return raw;
            }
        }

        private sealed class RunSnapshot
        {
            public int ActiveAtFire;
            public int CacheCountAfter;
            public int CacheCountBefore;
            public int CacheHits;
            public int Completed;
            public int DownloadingAtFire;
            public int DownloadsStarted;
            public float ElapsedMs;
            public int Failed;
            public int Issued;
            public int Merged;
            public int QueuedAtFire;
            public int Requests;
            public int Synchronous;
            public string Verdict;
        }

        private struct ConsumerState
        {
            public bool Done;
            public bool Failed;
            public bool Synchronous;
            public int TextureId;
            public int Frame;
            public float ElapsedSeconds;
        }
    }
}