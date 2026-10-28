using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace ImageDownloaderDemo.Demo.OwnerRefCountDemo
{
    /// <summary>
    ///     跨 owner 引用计数 Demo。
    ///     多个互相独立的 owner 请求同一个 URL，观察：
    ///     - 全局 refcount 如何随不同 owner 的请求累加；
    ///     - 销毁其中一个 owner 时，只释放它自己的那一份；
    ///     - refcount > 0 时 LRU 淘汰会跳过该纹理，归零后才会被淘汰。
    /// </summary>
    [AddComponentMenu("ImageDownloader Demo/Owner RefCount Demo")]
    public class OwnerRefCountDemo : MonoBehaviour
    {
        private const int OwnerSlotCount = 3;

        private static GUIStyle _titleStyle;
        private static GUIStyle _wrapStyle;
        private static GUIStyle _lineStyle;

        [Header("请求")] [SerializeField] private string url = "https://picsum.photos/seed/refcount/512";

        [Header("缓存压力")] [Tooltip("演示期间临时覆盖 ImageDownloader.maxCacheCount，退出 Demo 时恢复原值。")] [Range(1, 12)]
        [SerializeField]
        private int cacheLimit = 3;

        [Tooltip("一次 Load decoys / 场景中的 decoy 数量。decoy 不带 owner，因此不会持有引用。")] [Range(1, 32)]
        [SerializeField]
        private int decoyCount = 8;

        private readonly OwnerSlot[] _slots =
        {
            new OwnerSlot("A"),
            new OwnerSlot("B"),
            new OwnerSlot("C")
        };

        private readonly List<string> _log = new();

        private int _decoySerial;
        private bool _loadingDecoys;
        private Coroutine _decoyRoutine;
        private bool _logDirty;
        private Vector2 _logScroll;
        private Vector2 _pageScroll;
        private int _originalCacheLimit;
        private bool _restoreCacheLimit;
        private Coroutine _scenario;
        private bool _scenarioFinished;
        private bool _scenarioRunning;

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

        private void Start()
        {
            var downloader = ImageDownloader.Instance;
            _originalCacheLimit = downloader.maxCacheCount;
            _restoreCacheLimit = true;
            downloader.maxCacheCount = Mathf.Max(1, cacheLimit);
        }

        private void OnDestroy()
        {
            if (_scenario != null) StopCoroutine(_scenario);
            if (_decoyRoutine != null) StopCoroutine(_decoyRoutine);

            for (var i = 0; i < _slots.Length; i++)
                if (_slots[i].Owner != null)
                {
                    Destroy(_slots[i].Owner.gameObject);
                    _slots[i].Owner = null;
                }

            if (_restoreCacheLimit && ImageDownloader.HasInstance)
                ImageDownloader.Instance.maxCacheCount = _originalCacheLimit;
        }

        private void OnGUI()
        {
            var downloader = ImageDownloader.HasInstance ? ImageDownloader.Instance : null;
            var width = Mathf.Min(Screen.width - 24f, 900f);

            // 整页可滚动：小窗口下也能完整看到 owners 和日志，不会被屏幕底部裁掉。
            _pageScroll = GUILayout.BeginScrollView(_pageScroll, GUILayout.Width(width),
                GUILayout.Height(Screen.height - 8f));
            GUILayout.BeginVertical(GUILayout.Width(width - 20f));

            GUILayout.Label("<b>Cross-owner Reference Counting Demo</b>", TitleStyle);
            GUILayout.Label(
                "A / B 同时请求<b>同一个 URL</b>（refcount 2）。每次请求都会给这个 URL 的全局 refcount " +
                "<b>+1</b>（缓存命中也会），引用分别记在各 owner 的隐藏 tracker 上。" +
                "销毁 A 后，B 的引用仍钉着纹理，所以新 owner <b>C</b> 依然能拿到 cache hit；" +
                "直到 B、C 都销毁、refcount 归零并被 LRU 淘汰后，<b>A 重新请求</b>才会真正重新下载，" +
                "并拿到一个新的 Texture2D 实例。",
                WrapStyle);

            GUILayout.Space(6);
            DrawControls(downloader);
            GUILayout.Space(6);
            DrawSharedState(downloader);
            GUILayout.Space(6);
            DrawOwners();
            GUILayout.Space(6);
            DrawLog();

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        private void DrawControls(ImageDownloader downloader)
        {
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label("URL", GUILayout.Width(34));
            GUI.enabled = !_scenarioRunning;
            url = GUILayout.TextField(url ?? string.Empty);
            GUI.enabled = true;
            if (GUILayout.Button("New URL", GUILayout.Width(80)))
            {
                url = $"https://picsum.photos/seed/refcount{Random.Range(100000, 999999)}/512";
                ResetDemo();
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Cache limit: {cacheLimit}", GUILayout.Width(96));
            GUI.enabled = !_scenarioRunning;
            var newLimit = Mathf.RoundToInt(GUILayout.HorizontalSlider(cacheLimit, 1f, 12f, GUILayout.Width(170)));
            GUI.enabled = true;
            if (newLimit != cacheLimit)
            {
                cacheLimit = newLimit;
                if (downloader != null) downloader.maxCacheCount = cacheLimit;
            }

            GUILayout.Space(20);
            GUILayout.Label($"Decoys: {decoyCount}", GUILayout.Width(80));
            GUI.enabled = !_scenarioRunning && !_loadingDecoys;
            decoyCount = Mathf.RoundToInt(GUILayout.HorizontalSlider(decoyCount, 1f, 32f, GUILayout.Width(160)));
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = !_scenarioRunning && !string.IsNullOrWhiteSpace(url);
            if (GUILayout.Button("Run scenario", GUILayout.Height(28))) StartScenario();

            GUI.enabled = !_scenarioRunning && !_loadingDecoys && downloader != null;
            if (GUILayout.Button($"Load {decoyCount} decoys", GUILayout.Height(28)))
                _decoyRoutine = StartCoroutine(LoadDecoysRoutine(decoyCount));

            GUI.enabled = !_scenarioRunning;
            if (GUILayout.Button("Destroy all owners", GUILayout.Height(28))) DestroyAllOwners();
            if (GUILayout.Button("Clear cache + reset", GUILayout.Height(28))) ResetDemo();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (_scenarioRunning)
                GUILayout.Label("场景运行中…（控件已禁用）");
            else if (_loadingDecoys)
                GUILayout.Label("decoy 下载中…");
            else if (_scenarioFinished)
                GUILayout.Label(
                    "✔ 上一次场景已完整跑完（0–10 步）：refcount 归零、纹理被淘汰后，A 重新请求触发了真实下载。" +
                    "每一步都在下方日志里；可再次点 Run scenario 重跑。");
            else
                GUILayout.Label(
                    "手动路径：Spawn A → B（refcount 1 → 2）→ Load decoys（超出 cache limit）→ " +
                    "Destroy A（1，纹理仍存活）→ Load decoys → Spawn C（cache hit，2）→ Destroy B、C（1 → 0）→ " +
                    "Load decoys 触发淘汰 → Spawn A（重新下载）。",
                    WrapStyle);

            GUILayout.EndVertical();
        }

        private void DrawSharedState(ImageDownloader downloader)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<b>Shared URL state</b>", WrapStyle);

            if (downloader == null)
            {
                GUILayout.Label("ImageDownloader 尚未创建。Spawn 一个 owner 或点 Run scenario 即可创建单例。");
                GUILayout.EndVertical();
                return;
            }

            var refCount = downloader.GetCacheRefCount(url);
            var cached = downloader.IsCached(url);
            GUILayout.Label(
                $"cached = <b>{(cached ? "yes" : "no")}</b>    refcount = <b>{refCount}</b>    " +
                $"pinned = <b>{(refCount > 0 ? "yes" : "no")}</b>");
            GUILayout.Label(
                $"cache = {downloader.CacheCount}/{Mathf.Max(0, downloader.maxCacheCount)}    " +
                $"mem≈{downloader.CacheMemoryBytes / (1024f * 1024f):F2} MB    " +
                $"downloading = {downloader.DownloadingCount} (active {downloader.ActiveDownloadCount})");
            GUILayout.Label(
                $"downloads started = {downloader.TotalDownloadsStarted}    " +
                $"cache hits = {downloader.TotalCacheHits}    merged = {downloader.TotalMergedRequests}");

            var holders = new List<string>();
            var textureIds = new HashSet<int>();
            foreach (var slot in _slots)
            {
                if (slot.Owner == null) continue;
                holders.Add(slot.Label);
                if (slot.Owner.Texture != null) textureIds.Add(slot.Owner.Texture.GetInstanceID());
            }

            GUILayout.Label(
                $"live owners = {(holders.Count == 0 ? "-" : string.Join(", ", holders))}    " +
                $"distinct Texture2D = {textureIds.Count}");
            GUILayout.EndVertical();
        }

        private void DrawOwners()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<b>Owners</b>  — 每个 owner 每次请求持有 1 个缓存引用", WrapStyle);

            foreach (var slot in _slots)
                DrawOwnerRow(slot);

            GUILayout.EndVertical();
        }

        private void DrawOwnerRow(OwnerSlot slot)
        {
            GUILayout.BeginHorizontal(GUI.skin.box);

            if (slot.Owner == null)
            {
                GUI.enabled = !_scenarioRunning && !string.IsNullOrWhiteSpace(url);
                if (GUILayout.Button($"Spawn owner {slot.Label}", GUILayout.Width(130))) SpawnOwner(slot);
                GUI.enabled = true;
                GUILayout.Label("（未创建）");
            }
            else
            {
                var owner = slot.Owner;
                var rect = GUILayoutUtility.GetRect(48f, 48f, GUILayout.Width(48f), GUILayout.Height(48f));
                if (owner.Texture != null) GUI.DrawTexture(rect, owner.Texture, ScaleMode.ScaleToFit);
                else GUI.Box(rect, "…");

                GUILayout.BeginVertical();
                GUILayout.Label(
                    $"<b>{slot.Label}</b>  {owner.Status}    " +
                    $"tex = {(owner.Texture != null ? owner.Texture.GetInstanceID().ToString() : "-")}    " +
                    $"callback frame = {(owner.Completed ? owner.CallbackFrame.ToString() : "-")}");
                GUILayout.Label(owner.Url ?? "-", LineStyle);
                GUILayout.EndVertical();

                GUILayout.FlexibleSpace();
                GUI.enabled = !_scenarioRunning;
                if (GUILayout.Button($"Destroy {slot.Label}", GUILayout.Width(100))) DestroyOwner(slot);
                GUI.enabled = true;
            }

            GUILayout.EndHorizontal();
        }

        private void DrawLog()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label($"<b>Scenario log</b>  ({_log.Count} lines)", WrapStyle);

            // 最新一行始终显示在滚动区域外，避免用户以为脚本停在中间某一步。
            GUILayout.Label(
                _log.Count > 0 ? $"latest: {_log[_log.Count - 1]}" : "latest: （尚未运行）",
                LineStyle);

            if (_logDirty)
            {
                _logScroll.y = float.MaxValue;
                _logDirty = false;
            }

            _logScroll = GUILayout.BeginScrollView(_logScroll, GUILayout.Height(220f));
            if (_log.Count == 0)
                GUILayout.Label(
                    "点 “Run scenario” 自动演示：A 请求 → B 请求（2）→ 填满缓存 → Destroy A（1，C 之后仍能命中）" +
                    " → 加压力 → C 请求（cache hit，2）→ Destroy B、C（1 → 0）→ 加压力淘汰 → A 重新请求（真实下载）。",
                    LineStyle);
            foreach (var line in _log) GUILayout.Label(line, LineStyle);
            GUILayout.EndScrollView();

            GUILayout.EndVertical();
        }

        private RefCountOwner SpawnOwner(OwnerSlot slot)
        {
            if (slot.Owner != null) return slot.Owner;

            var go = new GameObject($"RefCountOwner_{slot.Label}");
            var owner = go.AddComponent<RefCountOwner>();
            owner.Configure(slot.Label);
            slot.Owner = owner;

            if (!string.IsNullOrWhiteSpace(url)) owner.RequestShared(url.Trim());
            return owner;
        }

        private void DestroyOwner(OwnerSlot slot, string reason = null)
        {
            if (slot.Owner == null) return;

            var label = slot.Label;
            Destroy(slot.Owner.gameObject);
            slot.Owner = null;

            Log(reason ?? $"Destroy owner {label} → 只释放它持有的缓存引用。");
        }

        private void DestroyAllOwners()
        {
            foreach (var slot in _slots)
                if (slot.Owner != null)
                {
                    Destroy(slot.Owner.gameObject);
                    slot.Owner = null;
                }
        }

        private void ResetDemo()
        {
            if (_scenario != null)
            {
                StopCoroutine(_scenario);
                _scenario = null;
            }

            if (_decoyRoutine != null)
            {
                StopCoroutine(_decoyRoutine);
                _decoyRoutine = null;
            }

            _scenarioRunning = false;
            _scenarioFinished = false;
            _loadingDecoys = false;
            DestroyAllOwners();

            if (ImageDownloader.HasInstance)
            {
                var downloader = ImageDownloader.Instance;
                downloader.CancelAll();
                downloader.ClearCache();
                downloader.maxCacheCount = Mathf.Max(1, cacheLimit);
                downloader.ResetStats();
            }

            _log.Clear();
            _logScroll = Vector2.zero;
            _logDirty = false;
        }

        private void StartScenario()
        {
            if (_scenarioRunning || string.IsNullOrWhiteSpace(url)) return;

            if (_decoyRoutine != null)
            {
                StopCoroutine(_decoyRoutine);
                _decoyRoutine = null;
                _loadingDecoys = false;
            }

            _scenarioFinished = false;
            _scenario = StartCoroutine(RunScenarioRoutine());
        }

        private IEnumerator RunScenarioRoutine()
        {
            _scenarioRunning = true;

            try
            {
                var downloader = ImageDownloader.Instance;
                _log.Clear();

                Log("=== 跨 owner 引用计数场景 ===");
                Log($"0. 重置：销毁所有 owner、清空缓存，cache limit = {cacheLimit}（从 {_originalCacheLimit} 临时覆盖）。");

                DestroyAllOwners();
                yield return null;

                downloader.CancelAll();
                downloader.ClearCache();
                downloader.maxCacheCount = Mathf.Max(1, cacheLimit);
                downloader.ResetStats();
                yield return null;

                Log(
                    $"   cache={downloader.CacheCount}  refcount={downloader.GetCacheRefCount(url)}  " +
                    $"cached={downloader.IsCached(url)}");

                // 1. owner A
                var ownerA = SpawnOwner(_slots[0]);
                yield return WaitForOwners(new[] { ownerA }, 60f);
                if (ownerA == null || ownerA.Texture == null)
                {
                    Log("✘ 共享 URL 下载失败（需要联网）。场景中止。");
                    yield break;
                }

                var textureA = ownerA.Texture;
                var originalTextureId = textureA.GetInstanceID();
                Log(
                    $"1. Owner A 请求共享 URL → cached={downloader.IsCached(url)}, " +
                    $"refcount={downloader.GetCacheRefCount(url)}（A 持有 1 个引用）。");

                // 2. owner B: same URL, same texture, refcount 2
                var ownerB = SpawnOwner(_slots[1]);
                yield return WaitForOwners(new[] { ownerB }, 30f);
                if (ownerB == null || ownerB.Texture == null)
                {
                    Log("✘ Owner B 没有拿到纹理。场景中止。");
                    yield break;
                }

                Log(
                    $"2. Owner B 请求同一个 URL → cache hit，same Texture2D={ReferenceEquals(textureA, ownerB.Texture)}，" +
                    $"refcount={downloader.GetCacheRefCount(url)}（A + B），" +
                    $"downloads started={downloader.TotalDownloadsStarted}, cache hits={downloader.TotalCacheHits}。");

                // 3. cache pressure while both owners pin the texture
                var flood = Mathf.Max(decoyCount, cacheLimit + 2);
                Log($"3. 加载 {flood} 个 decoy（无 owner，refcount 0），cache limit={cacheLimit}；共享纹理被 A+B 钉住。");
                yield return LoadDecoysRoutine(flood);
                Log(
                    $"   cache={downloader.CacheCount}/{downloader.maxCacheCount}  " +
                    $"shared cached={downloader.IsCached(url)}  refcount={downloader.GetCacheRefCount(url)}。");

                if (!downloader.IsCached(url))
                {
                    Log("✘ 共享纹理在仍有引用时被淘汰了，这与引用计数预期不符。场景中止。");
                    yield break;
                }

                if (downloader.CacheCount < Mathf.Min(cacheLimit, flood))
                    Log("⚠ decoy 没有填满缓存（下载失败？需要联网），后续淘汰结论可能不成立。");

                // 4. destroy A -> refcount 1
                DestroyOwner(_slots[0], "4. Destroy owner A → 只释放 A 的引用。");
                yield return null;
                Log(
                    $"   refcount={downloader.GetCacheRefCount(url)}  shared cached={downloader.IsCached(url)}" +
                    "（B 仍然钉住它）。");

                // 5. more pressure, still alive because of B
                var extra = Mathf.Max(1, flood / 2);
                Log($"5. 再加载 {extra} 个 decoy → refcount 仍为 1，共享纹理继续存活。");
                yield return LoadDecoysRoutine(extra);
                Log(
                    $"   cache={downloader.CacheCount}/{downloader.maxCacheCount}  " +
                    $"shared cached={downloader.IsCached(url)}  refcount={downloader.GetCacheRefCount(url)}。");

                // 6. new owner C joins while B still pins the cached texture -> cache hit
                var ownerC = SpawnOwner(_slots[2]);
                yield return WaitForOwners(new[] { ownerC }, 30f);
                if (ownerC == null || ownerC.Texture == null)
                {
                    Log("✘ Owner C 没有拿到纹理。场景中止。");
                    yield break;
                }

                Log(
                    $"6. Owner C 请求同一个 URL → cache hit（A 已销毁、缓存已被撑满，但纹理仍被 B 钉住），" +
                    $"same Texture2D={ReferenceEquals(ownerB.Texture, ownerC.Texture)}，" +
                    $"refcount={downloader.GetCacheRefCount(url)}（B + C）。");

                // 7. destroy B -> refcount 1
                DestroyOwner(_slots[1], "7. Destroy owner B → 只释放 B 的引用。");
                yield return null;
                Log(
                    $"   refcount={downloader.GetCacheRefCount(url)}  shared cached={downloader.IsCached(url)}" +
                    "（C 仍然钉住它）。");

                // 8. destroy C -> refcount 0, now evictable
                DestroyOwner(_slots[2], "8. Destroy owner C → 最后一个引用被释放。");
                yield return null;
                Log(
                    $"   refcount={downloader.GetCacheRefCount(url)}  shared cached={downloader.IsCached(url)}  " +
                    "（已不再钉住；如果缓存未超限，它会先留在这里）。");

                // 9. C's cache hit just moved the shared entry back to the LRU head, so it takes up to
                //    cacheLimit new entries to push it to the tail and then out of the cache (+2 margin).
                var evictPush = Mathf.Max(1, cacheLimit + 2);
                Log($"9. 再加载 {evictPush} 个 decoy → 把共享纹理重新推到 LRU 尾部并超限，随后被淘汰。");
                yield return LoadDecoysRoutine(evictPush);
                Log(
                    $"   shared cached={downloader.IsCached(url)}  cache={downloader.CacheCount}/{downloader.maxCacheCount}  " +
                    $"refcount={downloader.GetCacheRefCount(url)}。");

                if (downloader.IsCached(url))
                    Log("⚠ 共享纹理仍在缓存中，A 的重新请求可能仍会命中缓存；继续执行以便观察。");

                // 10. A (a fresh owner) requests the same URL after eviction -> a real re-download
                var cachedBeforeReload = downloader.IsCached(url);
                var downloadsBeforeReload = downloader.TotalDownloadsStarted;
                var hitsBeforeReload = downloader.TotalCacheHits;
                var ownerA2 = SpawnOwner(_slots[0]);
                yield return WaitForOwners(new[] { ownerA2 }, 60f);
                if (ownerA2 == null || ownerA2.Texture == null)
                {
                    Log("✘ Owner A 的重新下载失败（需要联网）。场景中止。");
                    yield break;
                }

                var reloadTextureId = ownerA2.Texture.GetInstanceID();
                Log(
                    $"10. Owner A 在纹理被淘汰后重新请求同一个 URL → cached-before-request={cachedBeforeReload}，" +
                    $"new downloads={downloader.TotalDownloadsStarted - downloadsBeforeReload}，" +
                    $"cache hits={downloader.TotalCacheHits - hitsBeforeReload}，" +
                    $"new Texture2D={reloadTextureId}（原实例={originalTextureId}，same={reloadTextureId == originalTextureId}），" +
                    $"refcount={downloader.GetCacheRefCount(url)}。");

                Log(reloadTextureId != originalTextureId
                    ? "✔ 引用归零 → 纹理被淘汰 → A 重新请求触发真实下载并拿到新的 Texture2D，说明淘汰是真实的。"
                    : "观察：A 拿到的仍是原实例（可能第 9 步没有真正淘汰缓存）。");

                _scenarioFinished = true;
            }
            finally
            {
                _scenarioRunning = false;
                _loadingDecoys = false;
                _scenario = null;
            }
        }

        private static IEnumerator WaitForOwners(RefCountOwner[] owners, float timeoutSeconds)
        {
            var elapsed = 0f;
            while (true)
            {
                var allDone = true;
                foreach (var owner in owners)
                    if (owner != null && !owner.Completed)
                    {
                        allDone = false;
                        break;
                    }

                if (allDone) yield break;

                elapsed += Time.unscaledDeltaTime;
                if (elapsed >= timeoutSeconds) yield break;
                yield return null;
            }
        }

        /// <summary>
        ///     加载一批唯一的 decoy URL。它们不使用 owner，因此 refcount 为 0，只负责填充缓存、
        ///     制造 LRU 压力。所有回调都返回（成功或失败）后结束。
        /// </summary>
        private IEnumerator LoadDecoysRoutine(int count)
        {
            _loadingDecoys = true;
            var downloader = ImageDownloader.Instance;
            var completed = 0;

            for (var i = 0; i < count; i++)
            {
                var decoyUrl = $"https://picsum.photos/seed/refcount-decoy{_decoySerial++}/256";
                downloader.GetTexture(decoyUrl, _ => completed++);
            }

            var elapsed = 0f;
            while (completed < count)
            {
                elapsed += Time.unscaledDeltaTime;
                if (elapsed > 45f)
                {
                    Log($"⚠ decoy 加载超时（{completed}/{count}），继续执行场景。");
                    break;
                }

                yield return null;
            }

            _loadingDecoys = false;
            _decoyRoutine = null;
        }

        private void Log(string message)
        {
            _log.Add(message);
            if (_log.Count > 200) _log.RemoveAt(0);
            _logDirty = true;
            Debug.Log("[RefCountDemo] " + message);
        }

        private sealed class OwnerSlot
        {
            public readonly string Label;
            public RefCountOwner Owner;

            public OwnerSlot(string label)
            {
                Label = label;
            }
        }
    }
}
