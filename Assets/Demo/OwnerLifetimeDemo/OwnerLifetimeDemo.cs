using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace ImageDownloaderDemo.Demo.OwnerLifetimeDemo
{
    /// <summary>
    ///     Owner 生命周期绑定 Demo。
    ///     同一帧对同一个 URL 发起 3 个请求，然后立即销毁其中两个发起者：
    ///     - A：绑定 owner，本帧销毁 → 回调被丢弃；
    ///     - B：绑定 owner，保持存活 → 正常收到回调；
    ///     - C：不绑定 owner，本帧销毁发起者 → 回调仍然送达。
    ///     三个请求共享同一次下载，因此可以排除“下载被取消”这一干扰因素：丢掉的只有 A 的回调。
    /// </summary>
    [AddComponentMenu("ImageDownloader Demo/Owner Lifetime Demo")]
    public class OwnerLifetimeDemo : MonoBehaviour
    {
        private const int MaxRecords = 16;

        private static GUIStyle _titleStyle;
        private static GUIStyle _wrapStyle;
        private static GUIStyle _lineStyle;

        [Header("请求")] [SerializeField] private string url = "https://picsum.photos/seed/owner-lifetime/768";

        [Header("手动请求")] [Tooltip("手动 Fire 后延迟多少秒销毁发起者。0 = 同一帧销毁。A/B/C 场景固定在同一帧销毁 A、C。")]
        [Range(0f, 3f)]
        [SerializeField]
        private float autoDestroyDelay;

        [Header("场景")] [Tooltip("等待回调的超时时间（秒）。")] [Range(5f, 60f)] [SerializeField]
        private float scenarioTimeout = 45f;

        private readonly List<string> _log = new();
        private readonly List<RequestRecord> _records = new();

        private int _generation;
        private bool _logDirty;
        private Vector2 _logScroll;
        private Vector2 _pageScroll;
        private int _recordSerial;
        private bool _scenarioFinished;
        private bool _scenarioRunning;
        private float _scenarioStartedAt;
        private string _scenarioVerdict;

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

        private void OnDestroy()
        {
            // 让尚未返回的回调失效；controller 自己也被销毁时，不再为清理动作写日志。
            _generation++;

            // Controller 单独被销毁时，也把自己创建的 actor 一起收掉；
            // owner 上的 tracker 会随 GameObject 一起销毁并完成释放。
            foreach (var record in _records)
                if (record.Actor != null)
                    Destroy(record.Actor.gameObject);
        }

        private void OnGUI()
        {
            var downloader = ImageDownloader.HasInstance ? ImageDownloader.Instance : null;
            var width = Mathf.Min(Screen.width - 24f, 980f);

            _pageScroll = GUILayout.BeginScrollView(_pageScroll, GUILayout.Width(width),
                GUILayout.Height(Screen.height - 8f));
            GUILayout.BeginVertical(GUILayout.Width(width - 20f));

            GUILayout.Label("<b>Owner Lifetime Binding Demo</b>", TitleStyle);
            GUILayout.Label(
                "把 MonoBehaviour 作为 <b>owner</b> 传给 <b>GetTexture / GetSprite</b> 后，下载器会在 owner 的 " +
                "GameObject 上挂一个隐藏 tracker。owner 销毁时，tracker 会把该 owner 从所有进行中的请求里移除，" +
                "所以它的回调<b>永远不会再触发</b>，同时释放它持有的缓存引用。\n" +
                "下面的 A/B/C 场景在同一帧请求同一个 URL（共享 1 次下载），然后立即销毁 A 和 C 的 GameObject：" +
                "<b>A</b> 绑定 owner 且被销毁 → 回调丢弃；<b>B</b> 绑定 owner 但保持存活 → 正常送达；" +
                "<b>C</b> 未绑定 owner，即使发起者被销毁 → 回调仍然送达。",
                WrapStyle);

            GUILayout.Space(6);
            DrawControls();
            GUILayout.Space(6);
            DrawLiveState(downloader);
            if (_scenarioRunning || _scenarioFinished)
            {
                GUILayout.Space(6);
                DrawScenarioState();
            }

            GUILayout.Space(6);
            DrawRecords();
            GUILayout.Space(6);
            DrawLog();

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        private void DrawControls()
        {
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label("URL", GUILayout.Width(34));
            GUI.enabled = !_scenarioRunning;
            url = GUILayout.TextField(url ?? string.Empty);
            if (GUILayout.Button("New URL", GUILayout.Width(80)))
            {
                url = $"https://picsum.photos/seed/owner-lifetime{Random.Range(100000, 999999)}/768";
                ResetManual();
            }

            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Auto-destroy: {autoDestroyDelay:0.00}s", GUILayout.Width(140));
            GUI.enabled = !_scenarioRunning;
            autoDestroyDelay = GUILayout.HorizontalSlider(autoDestroyDelay, 0f, 3f, GUILayout.Width(180));
            GUI.enabled = true;
            GUILayout.Label(
                autoDestroyDelay <= 0.0001f
                    ? "（Fire 后同一帧销毁）"
                    : "（网络很快时，回调可能在销毁之前就送达）",
                LineStyle);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = !_scenarioRunning && !string.IsNullOrWhiteSpace(url);
            if (GUILayout.Button("Run A/B/C scenario", GUILayout.Height(30))) StartCoroutine(RunScenario());
            if (GUILayout.Button("Fire bound owner", GUILayout.Height(30)))
                IssueRequest(true, autoDestroyDelay);
            if (GUILayout.Button("Fire unbound requester", GUILayout.Height(30)))
                IssueRequest(false, autoDestroyDelay);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = !_scenarioRunning;
            if (GUILayout.Button("Destroy all live actors", GUILayout.Height(26))) DestroyAllActors();
            if (GUILayout.Button("Clear cache + reset", GUILayout.Height(26))) ResetManual();
            GUI.enabled = true;
            GUILayout.Label(
                _scenarioRunning
                    ? "场景运行中…（控件已禁用）"
                    : "提示：先点 Run A/B/C scenario 看自动对照。Fire 会复用缓存：若 URL 已缓存，回调会同步送达" +
                      "（销毁发生在回调之后）；Auto-destroy=0 只对真正在途的请求产生丢弃效果。",
                WrapStyle);
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        private void DrawLiveState(ImageDownloader downloader)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<b>Live state</b>", WrapStyle);

            if (downloader == null)
            {
                GUILayout.Label("ImageDownloader 尚未创建；发起一个请求会自动创建单例。", LineStyle);
                GUILayout.EndVertical();
                return;
            }

            var currentUrl = string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim();
            GUILayout.Label(
                $"cached = <b>{(downloader.IsCached(currentUrl) ? "yes" : "no")}</b>    " +
                $"refcount = <b>{downloader.GetCacheRefCount(currentUrl)}</b>    " +
                $"cache = {downloader.CacheCount}    " +
                $"mem≈{downloader.CacheMemoryBytes / (1024f * 1024f):F2} MB");
            GUILayout.Label(
                $"downloading = {downloader.DownloadingCount} " +
                $"(active {downloader.ActiveDownloadCount}, queued {downloader.QueuedCount})    " +
                $"requests = {downloader.TotalRequests}    " +
                $"downloads started = {downloader.TotalDownloadsStarted}    " +
                $"merged = {downloader.TotalMergedRequests}    " +
                $"cache hits = {downloader.TotalCacheHits}");

            GUILayout.EndVertical();
        }

        private void DrawScenarioState()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<b>A/B/C scenario</b>", WrapStyle);
            if (_scenarioRunning)
                GUILayout.Label(
                    $"运行中…（已等待 {Time.realtimeSinceStartup - _scenarioStartedAt:F1}s / {scenarioTimeout:F0}s）",
                    WrapStyle);
            else if (!string.IsNullOrEmpty(_scenarioVerdict))
                GUILayout.Label(_scenarioVerdict, WrapStyle);
            GUILayout.EndVertical();
        }

        private void DrawRecords()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(
                $"<b>Requests</b>  ({_records.Count})  —  每一行是一次请求；Destroy 会立即销毁对应的 GameObject",
                WrapStyle);

            if (_records.Count == 0)
            {
                GUILayout.Label("还没有请求。点 Run A/B/C scenario 或上面的 Fire 按钮。", LineStyle);
            }
            else
            {
                foreach (var record in _records) DrawRecord(record);
            }

            GUILayout.EndVertical();
        }

        private void DrawRecord(RequestRecord record)
        {
            GUILayout.BeginHorizontal(GUI.skin.box);

            var rect = GUILayoutUtility.GetRect(56f, 56f, GUILayout.Width(56f), GUILayout.Height(56f));
            if (record.Texture != null) GUI.DrawTexture(rect, record.Texture, ScaleMode.ScaleToFit);
            else GUI.Box(rect, record.Bound ? "bound" : "free");

            GUILayout.BeginVertical();
            GUILayout.Label($"<b>{record.Label}</b>  {StatusOf(record)}", WrapStyle);
            GUILayout.Label(
                $"issued frame {record.IssuedFrame}    " +
                $"{(record.Synchronous ? "sync (cache hit)    " : "async    ")}" +
                $"destroy: {(record.DestroyRequested ? $"requested f{record.DestroyRequestedFrame}" : "-")}" +
                $" → {(record.ActorDestroyed ? $"done f{record.DestroyedFrame}" : "alive")}    " +
                $"callback: {(record.CallbackReceived ? $"f{record.CallbackFrame} (+{record.ElapsedMs:F0} ms)" : "-")}    " +
                $"tex: {(record.Texture != null ? record.Texture.GetInstanceID().ToString() : "-")}",
                LineStyle);
            GUILayout.Label(record.Url ?? "-", LineStyle);
            GUILayout.EndVertical();

            GUILayout.FlexibleSpace();

            GUI.enabled = record.Actor != null;
            if (GUILayout.Button("Destroy", GUILayout.Width(80f))) DestroyActor(record);
            GUI.enabled = true;

            GUILayout.EndHorizontal();
        }

        private static string StatusOf(RequestRecord record)
        {
            if (record.CallbackReceived)
                return record.Failed
                    ? "<color=#ff8080>callback: FAILED / null</color>"
                    : "<color=#7fd67f>callback: delivered</color>" +
                      (record.ActorDestroyed && !record.Bound ? "（发起者已销毁，但回调仍送达）" : string.Empty);

            if (record.Bound && record.DestroyRequested)
                return record.ActorDestroyed
                    ? "<color=#ffb060>callback: DROPPED (owner destroyed)</color>"
                    : "<color=#ffb060>callback: will be dropped (destroy queued)</color>";

            if (!record.Bound && record.DestroyRequested)
                return record.ActorDestroyed
                    ? "<color=#ffd27f>requester destroyed — callback still pending</color>"
                    : "requester destroy queued — no owner binding";

            return "waiting for callback…";
        }

        private void DrawLog()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label($"<b>Log</b>  ({_log.Count} lines)", WrapStyle);
            GUILayout.Label(_log.Count > 0 ? $"latest: {_log[_log.Count - 1]}" : "latest: （空）", LineStyle);

            // 新日志出现时自动滚到底部，避免看起来像卡在某一步。
            if (_logDirty)
            {
                _logScroll.y = float.MaxValue;
                _logDirty = false;
            }

            _logScroll = GUILayout.BeginScrollView(_logScroll, GUILayout.Height(240f));
            if (_log.Count == 0)
                GUILayout.Label("点 Run A/B/C scenario 查看完整步骤。", LineStyle);
            foreach (var line in _log) GUILayout.Label(line, LineStyle);
            GUILayout.EndScrollView();

            GUILayout.EndVertical();
        }

        /// <summary>
        ///     发起一次请求并按需销毁 actor。
        ///     <paramref name="destroyDelay" /> 小于 0 表示保持存活，等于 0 表示本帧销毁。
        /// </summary>
        private RequestRecord IssueRequest(bool bound, float destroyDelay, string fixedLabel = null,
            bool forceRefresh = false)
        {
            var downloader = ImageDownloader.Instance;
            var serial = ++_recordSerial;
            var label = fixedLabel ?? (bound ? $"B{serial}" : $"U{serial}");

            var go = new GameObject($"Lifetime{(bound ? "Owner" : "Requester")}_{label}");
            var actor = go.AddComponent<LifetimeDemoActor>();
            actor.Configure(label, bound);

            var record = new RequestRecord
            {
                Label = label,
                Bound = bound,
                Actor = actor,
                Url = url.Trim(),
                IssuedFrame = Time.frameCount,
                IssuedTime = Time.realtimeSinceStartup
            };

            _records.Add(record);
            TrimRecords();

            var generation = _generation;
            actor.Destroyed += _ => OnActorDestroyed(generation, record);

            Action<Texture2D> onResolved = texture => OnCallback(generation, record, texture);

            if (bound) downloader.GetTexture(record.Url, actor, onResolved, forceRefresh);
            else downloader.GetTexture(record.Url, onResolved, forceRefresh);

            // 缓存命中时回调是同步执行的，此时 callback 信息在 GetTexture 返回前就已写入。
            record.Synchronous = record.CallbackReceived;

            Log($"{label}: 发起{(bound ? " owner 绑定" : "无 owner")}请求 " +
                $"(frame {record.IssuedFrame}, forceRefresh={forceRefresh})。");

            if (destroyDelay >= 0f)
            {
                if (destroyDelay <= 0f) DestroyActor(record);
                else StartCoroutine(DestroyAfter(record, destroyDelay));
            }

            return record;
        }

        private void OnCallback(int generation, RequestRecord record, Texture2D texture)
        {
            if (generation != _generation) return;

            record.CallbackReceived = true;
            record.Failed = texture == null;
            record.CallbackFrame = Time.frameCount;
            record.ElapsedMs = (Time.realtimeSinceStartup - record.IssuedTime) * 1000f;
            record.Texture = texture;

            var ownerState = record.Bound
                ? record.ActorDestroyed
                    ? "owner 已销毁（不应该发生）"
                    : "owner 存活"
                : record.ActorDestroyed
                    ? "未绑定 owner；发起者已销毁"
                    : "未绑定 owner";

            Log($"{record.Label}: 回调送达（{(texture != null ? "ok" : "failed/null")}, " +
                $"frame {record.CallbackFrame}, +{record.ElapsedMs:F0} ms, {ownerState}）。");
        }

        private void OnActorDestroyed(int generation, RequestRecord record)
        {
            if (generation != _generation) return;

            record.Actor = null;
            record.ActorDestroyed = true;
            record.DestroyedFrame = Time.frameCount;

            string effect;
            if (record.Bound)
                effect = record.CallbackReceived
                    ? "回调已经送达过；现在只释放它持有的缓存引用。"
                    : "进行中的回调将被丢弃。";
            else
                effect = "未绑定 owner；下载器持有回调委托，回调不受影响。";

            Log($"{record.Label}: actor 销毁于 frame {record.DestroyedFrame} " +
                $"(bound={record.Bound}) → {effect}");
        }

        private void DestroyActor(RequestRecord record)
        {
            if (record.Actor == null) return;

            record.DestroyRequested = true;
            record.DestroyRequestedFrame = Time.frameCount;
            Log($"{record.Label}: 请求销毁 actor (frame {record.DestroyRequestedFrame})。");
            Destroy(record.Actor.gameObject);
        }

        private void DestroyAllActors()
        {
            var count = 0;
            foreach (var record in _records)
            {
                if (record.Actor == null) continue;
                record.DestroyRequested = true;
                record.DestroyRequestedFrame = Time.frameCount;
                Destroy(record.Actor.gameObject);
                count++;
            }

            Log($"已请求销毁 {count} 个仍存活的 actor。");
        }

        private IEnumerator DestroyAfter(RequestRecord record, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            DestroyActor(record);
        }

        private void TrimRecords()
        {
            while (_records.Count > MaxRecords)
            {
                var oldest = _records[0];
                _records.RemoveAt(0);
                if (oldest.Actor != null) Destroy(oldest.Actor.gameObject);
            }
        }

        private IEnumerator RunScenario()
        {
            if (_scenarioRunning || string.IsNullOrWhiteSpace(url)) yield break;

            _scenarioRunning = true;
            _scenarioFinished = false;
            _scenarioVerdict = null;
            _scenarioStartedAt = Time.realtimeSinceStartup;

            ResetRequests();
            _log.Clear();
            yield return null;

            Log("=== Owner 生命周期绑定 A/B/C 场景 ===");
            Log("同一帧对同一个 URL 发起 3 个请求；A 和 C 的 GameObject 会在本帧结束时销毁。");

            var downloader = ImageDownloader.Instance;
            var downloads0 = downloader.TotalDownloadsStarted;
            var merged0 = downloader.TotalMergedRequests;
            var hits0 = downloader.TotalCacheHits;

            // A：绑定 owner，且在回调前销毁 -> 回调应被丢弃。
            var a = IssueRequest(true, 0f, "A", true);
            // B：绑定 owner，保持存活 -> 回调应正常送达。
            var b = IssueRequest(true, -1f, "B", true);
            // C：不绑定 owner，发起者同样在本帧销毁 -> 回调仍应送达。
            var c = IssueRequest(false, 0f, "C", true);

            yield return null; // 让 Destroy() 在本帧结束时真正执行

            var downloads = downloader.TotalDownloadsStarted - downloads0;
            var merged = downloader.TotalMergedRequests - merged0;
            var hits = downloader.TotalCacheHits - hits0;

            Log($"3 个请求已发出：downloads started={downloads}, merged={merged}, cache hits={hits}" +
                "（预期 1 / 2 / 0：三个 waiter 共享同一次传输）。");

            var waitStart = Time.realtimeSinceStartup;
            while (!b.CallbackReceived || !c.CallbackReceived)
            {
                if (Time.realtimeSinceStartup - waitStart > scenarioTimeout) break;
                yield return null;
            }

            // 再等半秒，给“本应被丢弃”的 A 回调充足的到达机会。
            yield return new WaitForSecondsRealtime(0.5f);

            var bDelivered = b.CallbackReceived && b.Texture != null;
            var cDelivered = c.CallbackReceived && c.Texture != null;
            var aDropped = !a.CallbackReceived;
            var actorsDestroyed = a.ActorDestroyed && c.ActorDestroyed;
            var sameTexture = b.Texture != null && c.Texture != null && ReferenceEquals(b.Texture, c.Texture);

            Log($"A: callback={(a.CallbackReceived ? "received" : "dropped")}    " +
                $"B: callback={(b.CallbackReceived ? "received" : "not received")}    " +
                $"C: callback={(c.CallbackReceived ? "received" : "not received")}");
            Log($"shared transfer: downloads={downloads}, merged={merged}, cacheHits={hits}; " +
                $"B/C same Texture2D={sameTexture}; A owner destroyed={a.ActorDestroyed}; " +
                $"C requester destroyed={c.ActorDestroyed}.");

            if (aDropped && bDelivered && cDelivered && actorsDestroyed && sameTexture && downloads == 1 &&
                merged == 2 && hits == 0)
            {
                _scenarioVerdict =
                    "✔ PASS — 1 次下载、2 次合并、3 个 waiter：A 的 owner 在回调前销毁，回调被丢弃；" +
                    "B 与 C 收到同一张纹理。C 的发起者也被销毁，但因为没绑定 owner，它的回调照常送达。";
            }
            else
            {
                _scenarioVerdict = "✘ 未完全符合预期：" +
                                   $"A callback received={a.CallbackReceived}, B delivered={bDelivered}, " +
                                   $"C delivered={cDelivered}, A/C actors destroyed={actorsDestroyed}, " +
                                   $"same texture={sameTexture}, downloads={downloads}, merged={merged}, " +
                                   $"cacheHits={hits}。";
                if (!bDelivered || !cDelivered) _scenarioVerdict += "（下载可能失败或超时，需要联网。）";
                if (a.CallbackReceived) _scenarioVerdict += " A 的回调在销毁前后仍然送达了。";
                if (!actorsDestroyed) _scenarioVerdict += " A/C 的 GameObject 没有按预期销毁。";
            }

            Log(_scenarioVerdict);
            _scenarioFinished = true;
            _scenarioRunning = false;
        }

        private void ResetManual()
        {
            _scenarioFinished = false;
            _scenarioVerdict = null;
            ResetRequests();
            _log.Clear();
            Log("已重置：销毁所有 actor、取消下载、清空缓存与计数器。");
        }

        private void ResetRequests()
        {
            _generation++; // 让旧的、尚未返回的回调失效

            foreach (var record in _records)
                if (record.Actor != null)
                    Destroy(record.Actor.gameObject);
            _records.Clear();

            if (ImageDownloader.HasInstance)
            {
                var downloader = ImageDownloader.Instance;
                downloader.CancelAll();
                downloader.ClearCache();
                downloader.ResetStats();
            }

            _logDirty = true;
        }

        private void Log(string message)
        {
            _log.Add(message);
            if (_log.Count > 300) _log.RemoveAt(0);
            _logDirty = true;
            Debug.Log("[LifetimeDemo] " + message);
        }

        private sealed class RequestRecord
        {
            public LifetimeDemoActor Actor;
            public bool ActorDestroyed;
            public bool Bound;
            public int CallbackFrame;
            public bool CallbackReceived;
            public int DestroyedFrame;
            public bool DestroyRequested;
            public int DestroyRequestedFrame;
            public float ElapsedMs;
            public bool Failed;
            public int IssuedFrame;
            public float IssuedTime;
            public string Label;
            public bool Synchronous;
            public Texture2D Texture;
            public string Url;
        }
    }
}
