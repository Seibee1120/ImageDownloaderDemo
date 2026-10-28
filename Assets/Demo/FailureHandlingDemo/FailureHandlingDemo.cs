using System;
using System.Collections.Generic;
using UnityEngine;

namespace ImageDownloaderDemo.Demo.FailureHandlingDemo
{
    /// <summary>
    ///     超时 / 重试 / 解码失败 / HTTP 失败分支 Demo。
    ///
    ///     场景里会启动一个只监听 127.0.0.1 的“恶意”HTTP 服务器，故意制造：
    ///     - 连接后长时间不返回（触发客户端 timeout）；
    ///     - HTTP 500 / 404；
    ///     - HTTP 200 + image/png，但响应体不是图片（触发解码失败）。
    ///
    ///     这些情况在 ImageDownloader.DownloadRoutine 里走的是同一条失败路径：
    ///     本次尝试失败 -> attempt++ -> attempt <= maxRetry 就等待 retryDelay 后重试，否则 Finish(null)。
    ///     调用方最终只会收到 Texture2D 或 null，中间的重试只体现在 Console 与服务器日志里。
    /// </summary>
    [AddComponentMenu("ImageDownloader Demo/Failure Handling Demo")]
    public class FailureHandlingDemo : MonoBehaviour
    {
        private const float UiWidth = 980f;
        private const string FallbackPngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAASSURBVBhXY4ha9f8/MmYgXQAAUr4wIbW/6A8AAAAASUVORK5CYII=";

        private static readonly Scenario[] Scenarios =
        {
            new Scenario(ScenarioKind.Happy, "① 成功基线（200 + PNG）", "/ok", 2f, 0, 0.25f,
                "1 次尝试 → 200 + 有效 PNG → 成功回调"),
            new Scenario(ScenarioKind.Http500, "② HTTP 500 → 重试耗尽", "/http-500", 2f, 2, 0.25f,
                "3 次尝试全部 500 → 3 次失败 → 最终 null"),
            new Scenario(ScenarioKind.Flaky, "③ 500×2 → 200（重试恢复）", "/flaky?fail=2", 2f, 2, 0.25f,
                "前 2 次 500，第 3 次 200 → 成功回调"),
            new Scenario(ScenarioKind.Decode, "④ 解码失败（200 + 非图片）", "/garbage", 2f, 1, 0.25f,
                "2 次 200 + image/png，但响应体不是图片 → 解码失败 → 最终 null"),
            new Scenario(ScenarioKind.Timeout, "⑤ 超时 0.8s → 重试耗尽", "/slow?ms=5000", 0.8f, 1, 0.25f,
                "2 次尝试各在 ~0.8s 被中止 → 最终 null"),
            new Scenario(ScenarioKind.NotFound, "⑥ HTTP 404 → 重试耗尽", "/http-404", 2f, 2, 0.25f,
                "3 次尝试全部 404 → 3 次失败 → 最终 null")
        };

        private static GUIStyle _titleStyle;
        private static GUIStyle _wrapStyle;
        private static GUIStyle _lineStyle;

        [Header("并发等待者")]
        [Tooltip("同一帧对同一个 URL 发起的请求数。>1 时仍然只有一次传输与一套重试，所有等待者共享最终结果。")]
        [Range(1, 4)]
        [SerializeField]
        private int waiterCount = 1;

        private bool _configCaptured;
        private int _lastLogCount = -1;
        private Vector2 _logScroll;
        private float _originalRetryDelay;
        private int _originalMaxRetry;
        private float _originalTimeout;
        private Vector2 _pageScroll;
        private RunState _run;
        private int _runId;
        private LocalMockImageServer _server;
        private string _serverError;

        private bool Pending => _run != null && !_run.Finalized;

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
            _originalTimeout = downloader.timeoutSeconds;
            _originalMaxRetry = downloader.maxRetry;
            _originalRetryDelay = downloader.retryDelaySeconds;
            _configCaptured = true;

            EnsureServer();
        }

        private void OnDestroy()
        {
            if (ImageDownloader.HasInstance && Pending && _run != null)
                ImageDownloader.Instance.Cancel(_run.Url);

            _server?.Dispose();
            _server = null;

            if (_configCaptured && ImageDownloader.HasInstance)
            {
                var downloader = ImageDownloader.Instance;
                downloader.timeoutSeconds = _originalTimeout;
                downloader.maxRetry = _originalMaxRetry;
                downloader.retryDelaySeconds = _originalRetryDelay;
            }
        }

        private void Update()
        {
            // 最后一条超时尝试的“客户端断开”由服务器线程在 ~20ms 轮询中写入；
            // 客户端回调可能先到，所以完结后再刷新一小段时间，避免日志永远停在
            // "waiting for response…"、aborted 少计一次。
            if (_run == null || !_run.Finalized || _server == null) return;
            if (Time.realtimeSinceStartup - _run.FinalizedRealtime > 1.5f) return;

            var live = _server.Snapshot();
            _run.Server = live;
            _run.Verdict = BuildVerdict(_run, live);
        }

        private void OnGUI()
        {
            var downloader = ImageDownloader.HasInstance ? ImageDownloader.Instance : null;
            var width = Mathf.Min(Screen.width - 24f, UiWidth);

            _pageScroll = GUILayout.BeginScrollView(_pageScroll, GUILayout.Width(width),
                GUILayout.Height(Screen.height - 8f));
            GUILayout.BeginVertical(GUILayout.Width(width - 20f));

            GUILayout.Label("<b>Timeout / Retry / Failure Branch Demo</b>", TitleStyle);
            GUILayout.Label(
                "本 Demo 在 <b>127.0.0.1</b> 起了一个只监听回环地址的模拟服务器，故意制造 " +
                "<b>超时</b>（连接后不返回）、<b>HTTP 5xx / 4xx</b>，以及 " +
                "<b>200 + image/png 但响应体不是图片</b> 的<b>解码失败</b>。\n" +
                "这些情况在 DownloadRoutine 里走的是同一条失败路径：本次尝试失败 → attempt++ → " +
                "attempt ≤ maxRetry 就等待 retryDelay 后重试，否则 Finish(null)。" +
                "调用者最终只看到 Texture2D 或 null；中间的每次重试只出现在 Console 和下方的服务器日志里。" +
                "注意 runtime 不区分 4xx / 5xx / 超时 / 解码失败，<b>所有失败都会重试</b>。",
                WrapStyle);

            GUILayout.Space(6);
            DrawControls(downloader);
            GUILayout.Space(6);
            DrawServerState(downloader);
            GUILayout.Space(6);
            DrawRun(downloader);
            GUILayout.Space(6);
            DrawAttemptLog();
            GUILayout.Space(6);
            DrawCallbacks();

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        private void DrawControls(ImageDownloader downloader)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(
                "<b>选择场景</b> — 每次运行都会临时覆盖 downloader 的 timeoutSeconds / maxRetry / retryDelaySeconds" +
                "（退出 Demo 时恢复原值）。",
                WrapStyle);

            var canRun = !Pending && _server != null;
            GUI.enabled = canRun;
            for (var row = 0; row < 2; row++)
            {
                GUILayout.BeginHorizontal();
                for (var column = 0; column < 3; column++)
                {
                    var scenario = Scenarios[row * 3 + column];
                    if (GUILayout.Button(scenario.Label, GUILayout.Height(34))) StartRun(scenario);
                }

                GUILayout.EndHorizontal();
            }

            GUI.enabled = true;

            GUILayout.BeginHorizontal();
            GUI.enabled = !Pending;
            GUILayout.Label($"并发等待者: {waiterCount}", GUILayout.Width(120));
            waiterCount = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(waiterCount, 1f, 4f, GUILayout.Width(140)));
            GUI.enabled = true;

            GUILayout.Label(
                waiterCount > 1
                    ? "同一 URL 只下载/重试一次，全部等待者收到同一个结果。"
                    : "每个 run 使用唯一 URL，不会命中缓存。",
                LineStyle, GUILayout.Width(300));
            GUILayout.FlexibleSpace();

            GUI.enabled = Pending;
            if (GUILayout.Button("取消当前 run", GUILayout.Width(120))) CancelRun();
            GUI.enabled = !Pending;

            if (GUILayout.Button("清空缓存", GUILayout.Width(90))) downloader?.ClearCache();
            if (GUILayout.Button("重置统计", GUILayout.Width(90))) downloader?.ResetStats();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (Pending)
                GUILayout.Label(
                    $"运行中… callbacks {_run.Completed}/{_run.Issued}。控件在本次 run 结束前锁定；可以点“取消当前 run”验证取消分支。",
                    WrapStyle);
            else if (_run != null)
                GUILayout.Label("✔ 上一个场景已结束。看下方 <b>verdict</b> 与服务器日志；可以换场景继续。", WrapStyle);
            else
                GUILayout.Label(
                    "建议顺序：① 先确认成功基线 → ②/⑥ 看 HTTP 失败 → ③ 看重试恢复 → " +
                    "④ 看 HTTP 200 但解码失败 → ⑤ 看超时；Console 里能看到每次 attempt 的 warning/error。",
                    WrapStyle);

            GUILayout.EndVertical();
        }

        private void DrawServerState(ImageDownloader downloader)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<b>本地模拟服务器</b>", WrapStyle);

            if (_server == null)
            {
                GUILayout.Label(
                    string.IsNullOrEmpty(_serverError) ? "模拟服务器尚未启动…" : $"启动失败：{_serverError}",
                    WrapStyle);
                if (GUILayout.Button("重试启动", GUILayout.Width(100)))
                {
                    _serverError = null;
                    EnsureServer();
                }
            }
            else
            {
                GUILayout.Label(
                    $"http://127.0.0.1:{_server.Port}  —  只绑定 loopback，请求不离开本机。", WrapStyle);

                var snapshot = _run != null && _run.Finalized ? _run.Server : _server.Snapshot();
                GUILayout.Label(
                    $"本次 run（服务器视角）: attempts=<b>{snapshot.Attempts}</b>    " +
                    $"2xx={snapshot.Status2xx}  4xx={snapshot.Status4xx}  5xx={snapshot.Status5xx}  " +
                    $"aborted={snapshot.Aborted}",
                    WrapStyle);
            }

            if (downloader != null)
                GUILayout.Label(
                    $"session: requests={downloader.TotalRequests}  starts={downloader.TotalDownloadsStarted}  " +
                    $"merged={downloader.TotalMergedRequests}  cacheHits={downloader.TotalCacheHits}  " +
                    $"active={downloader.ActiveDownloadCount}/{Mathf.Max(1, downloader.maxConcurrentDownloads)}  " +
                    $"queued={downloader.QueuedCount}",
                    LineStyle);

            GUILayout.EndVertical();
        }

        private void DrawRun(ImageDownloader downloader)
        {
            if (_run == null) return;
            var run = _run;
            var snapshot = run.Finalized ? run.Server : _server != null ? _server.Snapshot() : null;
            var elapsed = run.Finalized
                ? run.ElapsedMs
                : (Time.realtimeSinceStartup - run.StartedRealtime) * 1000f;

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<b>本次 run</b>", WrapStyle);
            GUILayout.Label($"场景: <b>{run.Scenario.Label}</b>", WrapStyle);
            GUILayout.Label($"预期: {run.Scenario.Expectation}", WrapStyle);
            GUILayout.Label($"URL: {run.Url}", LineStyle);
            GUILayout.Label(
                $"临时配置: timeout={run.Scenario.Timeout:F2}s  maxRetry={run.Scenario.MaxRetry}  " +
                $"retryDelay={run.Scenario.RetryDelay:F2}s  →  预期尝试 {run.Scenario.ExpectedAttempts} 次 " +
                $"(1 次初始 + {run.Scenario.MaxRetry} 次重试)");
            GUILayout.Label(
                $"状态: <b>{(run.Finalized ? run.Cancelled ? "cancelled" : "complete" : "waiting…")}</b>    " +
                $"callbacks {run.Completed}/{run.Issued}    elapsed {elapsed:F0} ms");

            if (snapshot != null)
                GUILayout.Label(
                    $"服务器观察: attempts=<b>{snapshot.Attempts}</b>  2xx={snapshot.Status2xx}  " +
                    $"4xx={snapshot.Status4xx}  5xx={snapshot.Status5xx}  aborted={snapshot.Aborted}");

            if (run.Finalized)
                GUILayout.Label(
                    $"本 run 计数: requests={run.RunRequests}  starts={run.RunStarts}  " +
                    $"merged={run.RunMerged}  cacheHits={run.RunCacheHits}" +
                    (run.Issued > 1 && run.RunStarts == 1
                        ? "   （starts=1 说明 N 个等待者共享同一条下载/重试链）"
                        : string.Empty));

            GUILayout.Label($"回调结果: ok={run.Success}  null={run.Failed}" +
                            (run.Cancelled ? "  （用户取消）" : string.Empty));

            if (run.Preview != null)
            {
                GUILayout.BeginHorizontal();
                var rect = GUILayoutUtility.GetRect(64f, 64f, GUILayout.Width(64f), GUILayout.Height(64f));
                GUI.DrawTexture(rect, run.Preview, ScaleMode.ScaleToFit);
                GUILayout.Label("成功回调实际收到的 Texture2D。", LineStyle);
                GUILayout.EndHorizontal();
            }

            if (run.Finalized && !string.IsNullOrEmpty(run.Verdict))
                GUILayout.Label($"<b>{run.Verdict}</b>", WrapStyle);

            GUILayout.EndVertical();
        }

        private void DrawAttemptLog()
        {
            if (_run == null) return;

            var snapshot = _run.Finalized ? _run.Server : _server?.Snapshot();
            var entries = snapshot?.Entries ?? Array.Empty<AttemptLogEntry>();

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label($"<b>服务器端尝试日志</b> — 每个 TCP 请求一行（{entries.Length}）", WrapStyle);
            GUILayout.Label(
                "时间从点击场景时算起。这是服务器视角，所以能看到“客户端在等待 N ms 后提前断开”的超时证据。",
                LineStyle);

            if (_lastLogCount != entries.Length)
            {
                _logScroll.y = float.MaxValue;
                _lastLogCount = entries.Length;
            }

            _logScroll = GUILayout.BeginScrollView(_logScroll, GUILayout.Height(180f));
            if (entries.Length == 0)
            {
                GUILayout.Label("（等待请求…）", LineStyle);
            }
            else
            {
                foreach (var entry in entries) GUILayout.Label(FormatEntry(entry), LineStyle);
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawCallbacks()
        {
            if (_run == null || _run.Callbacks.Count == 0) return;

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(
                $"<b>调用者回调</b> — {_run.Success} 个拿到 Texture2D，{_run.Failed} 个拿到 null", WrapStyle);

            foreach (var callback in _run.Callbacks)
            {
                var line = callback.Success
                    ? $"#{callback.Index}  ok    tex={callback.TextureId}  frame={callback.Frame}  +{callback.ElapsedMs:F0}ms"
                    : $"#{callback.Index}  null  (失败 / 超时 / 解码失败 / 取消)  frame={callback.Frame}  +{callback.ElapsedMs:F0}ms";
                GUILayout.Label(line, LineStyle);
            }

            GUILayout.EndVertical();
        }

        private void StartRun(Scenario scenario)
        {
            if (!ImageDownloader.HasInstance) return;
            if (_server == null && !EnsureServer()) return;

            var downloader = ImageDownloader.Instance;

            // 正常情况下按钮在 run 期间被禁用；这里仍做一次兜底，保证旧请求不会污染新实验。
            if (Pending) CancelRun();

            downloader.timeoutSeconds = scenario.Timeout;
            downloader.maxRetry = scenario.MaxRetry;
            downloader.retryDelaySeconds = scenario.RetryDelay;

            _server.BeginRun();
            _runId++;
            var runId = _runId;

            var run = new RunState
            {
                RunId = runId,
                Scenario = scenario,
                // runId 让每次运行的 URL 都是唯一的：既避免缓存命中，也避免与上一轮合并。
                Url = BuildUrl(scenario, runId),
                Issued = waiterCount,
                StartedRealtime = Time.realtimeSinceStartup,
                CallbackDone = new bool[waiterCount],
                BaseRequests = downloader.TotalRequests,
                BaseStarts = downloader.TotalDownloadsStarted,
                BaseMerged = downloader.TotalMergedRequests,
                BaseCacheHits = downloader.TotalCacheHits
            };

            _run = run;
            _lastLogCount = -1;
            _logScroll = Vector2.zero;

            for (var i = 0; i < waiterCount; i++)
            {
                var index = i;
                downloader.GetTexture(run.Url, this, texture => OnResolved(runId, index, texture));
            }
        }

        private void OnResolved(int runId, int index, Texture2D texture)
        {
            var run = _run;
            if (run == null || run.Finalized || runId != run.RunId) return;
            if (index < 0 || index >= run.CallbackDone.Length || run.CallbackDone[index]) return;

            run.CallbackDone[index] = true;
            run.Completed++;

            var elapsedMs = (Time.realtimeSinceStartup - run.StartedRealtime) * 1000f;
            run.Callbacks.Add(new CallbackRecord
            {
                Index = index,
                Success = texture != null,
                TextureId = texture != null ? texture.GetInstanceID() : 0,
                Frame = Time.frameCount,
                ElapsedMs = elapsedMs
            });

            if (texture != null)
            {
                run.Success++;
                if (run.Preview == null) run.Preview = texture;
            }
            else
            {
                run.Failed++;
            }

            if (run.Completed >= run.Issued) FinalizeRun(run);
        }

        private void CancelRun()
        {
            if (_run == null || _run.Finalized) return;

            var run = _run;
            run.Cancelled = true;

            // Cancel 会同步把所有等待者以 null 回调；先让这些回调按真实情况记入 callbacks 列表。
            if (ImageDownloader.HasInstance) ImageDownloader.Instance.Cancel(run.Url);

            // 极端情况下（例如等待者的 owner 已被销毁，回调被丢弃）不会走到最后一次 OnResolved，
            // 这里强制收尾，避免界面永远停在 waiting。
            if (!run.Finalized)
            {
                run.Completed = run.Issued;
                FinalizeRun(run);
            }
        }

        private void FinalizeRun(RunState run)
        {
            if (run.Finalized) return;

            run.Finalized = true;
            run.FinalizedRealtime = Time.realtimeSinceStartup;
            run.ElapsedMs = (Time.realtimeSinceStartup - run.StartedRealtime) * 1000f;
            run.Server = _server != null ? _server.Snapshot() : new ServerSnapshot();
            run.Verdict = BuildVerdict(run, run.Server);

            if (ImageDownloader.HasInstance)
            {
                var downloader = ImageDownloader.Instance;
                run.RunRequests = Mathf.Max(0, downloader.TotalRequests - run.BaseRequests);
                run.RunStarts = Mathf.Max(0, downloader.TotalDownloadsStarted - run.BaseStarts);
                run.RunMerged = Mathf.Max(0, downloader.TotalMergedRequests - run.BaseMerged);
                run.RunCacheHits = Mathf.Max(0, downloader.TotalCacheHits - run.BaseCacheHits);
            }

            _lastLogCount = -1;
        }

        private string BuildVerdict(RunState run, ServerSnapshot server)
        {
            if (run.Cancelled)
                return "■ CANCELLED — 用户取消；所有等待者立即收到 null，后续重试不再发生。";

            var expected = run.Scenario.ExpectedAttempts;
            var attemptsOk = server.Attempts == expected;
            var allSuccess = run.Success == run.Issued && run.Failed == 0;
            var allNull = run.Failed == run.Issued && run.Success == 0;

            switch (run.Scenario.Kind)
            {
                case ScenarioKind.Happy:
                    return allSuccess && server.Attempts == 1 && server.Status2xx == 1
                        ? "✔ 成功路径：1 次尝试 → 200 + 有效 PNG → 解码成功，未触发重试。"
                        : $"✘ 与预期不符：attempts={server.Attempts}/1, 2xx={server.Status2xx}, " +
                          $"ok={run.Success}, null={run.Failed}。";

                case ScenarioKind.Http500:
                case ScenarioKind.NotFound:
                {
                    var code = run.Scenario.Kind == ScenarioKind.Http500 ? "500" : "404";
                    var statusOk = run.Scenario.Kind == ScenarioKind.Http500
                        ? server.Status5xx == expected
                        : server.Status4xx == expected;
                    return allNull && attemptsOk && statusOk
                        ? $"✔ HTTP 失败分支：{expected} 次尝试全部返回 HTTP {code}；重试耗尽后回调 null。"
                        : $"✘ 与预期不符：attempts={server.Attempts}/{expected}, " +
                          $"4xx={server.Status4xx}, 5xx={server.Status5xx}, ok={run.Success}, null={run.Failed}。";
                }

                case ScenarioKind.Flaky:
                    return allSuccess && server.Status5xx == 2 && server.Status2xx == 1 &&
                           server.Attempts == 3
                        ? $"✔ 重试恢复：前 2 次 500 → 第 3 次 200 → 解码成功；{run.Issued} 个等待者都拿到同一纹理。"
                        : $"✘ 与预期不符：attempts={server.Attempts}/3, 5xx={server.Status5xx}, " +
                          $"2xx={server.Status2xx}, ok={run.Success}, null={run.Failed}。";

                case ScenarioKind.Decode:
                    return allNull && attemptsOk && server.Status2xx == expected
                        ? $"✔ 解码失败：{expected} 次都收到 200 + image/png，但响应体无法生成 Texture2D；" +
                          "重试后最终 null。"
                        : $"✘ 与预期不符：attempts={server.Attempts}/{expected}, 2xx={server.Status2xx}, " +
                          $"ok={run.Success}, null={run.Failed}。";

                case ScenarioKind.Timeout:
                    return allNull && attemptsOk
                        ? $"✔ 超时分支：{expected} 次尝试都在 ~{run.Scenario.Timeout:F2}s 内被客户端中止" +
                          $"（服务器日志 aborted={server.Aborted} 为佐证）；最终 null。"
                        : $"✘ 与预期不符：attempts={server.Attempts}/{expected}, aborted={server.Aborted}, " +
                          $"ok={run.Success}, null={run.Failed}。";
            }

            return string.Empty;
        }

        private string BuildUrl(Scenario scenario, int runId)
        {
            var separator = scenario.Path.Contains("?") ? "&" : "?";
            return $"http://127.0.0.1:{_server.Port}{scenario.Path}{separator}run={runId}";
        }

        private bool EnsureServer()
        {
            if (_server != null) return true;
            if (_serverError != null) return false;

            try
            {
                _server = new LocalMockImageServer(CreatePngBytes());
                return true;
            }
            catch (Exception e)
            {
                _serverError = $"{e.GetType().Name}: {e.Message}";
                Debug.LogError($"[FailureHandlingDemo] 无法启动本地模拟服务器: {e}");
                return false;
            }
        }

        /// <summary>
        ///     用 Unity 自己编码一张小 PNG，作为成功路径的响应体；
        ///     如果当前平台不支持 EncodeToPNG，则退回一段已验证的 4x4 PNG。
        /// </summary>
        private static byte[] CreatePngBytes()
        {
            try
            {
                const int size = 16;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                var pixels = new Color32[size * size];

                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                    pixels[y * size + x] = (x / 4 + y / 4) % 2 == 0
                        ? new Color32(90, 170, 255, 255)
                        : new Color32(20, 30, 50, 255);

                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                var png = texture.EncodeToPNG();
                Destroy(texture);

                if (png != null && png.Length > 0) return png;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FailureHandlingDemo] EncodeToPNG failed, using fallback: {e.Message}");
            }

            return Convert.FromBase64String(FallbackPngBase64);
        }

        private static string FormatEntry(AttemptLogEntry entry)
        {
            string color;
            if (entry.Aborted || entry.Status >= 500 || entry.Status == 404)
                color = "#ff8080";
            else if (entry.Status >= 200 && entry.Status < 300 && entry.Outcome.Contains("NOT a PNG"))
                color = "#ffcc66";
            else
                color = "#8fdc8f";

            return $"<color={color}>+{entry.StartedMs / 1000f,5:F2}s  {entry.Path,-12}  {entry.Outcome}</color>";
        }

        private enum ScenarioKind
        {
            Happy,
            Http500,
            Flaky,
            Decode,
            Timeout,
            NotFound
        }

        private readonly struct Scenario
        {
            public Scenario(ScenarioKind kind, string label, string path, float timeout, int maxRetry,
                float retryDelay, string expectation)
            {
                Kind = kind;
                Label = label;
                Path = path;
                Timeout = timeout;
                MaxRetry = maxRetry;
                RetryDelay = retryDelay;
                Expectation = expectation;
            }

            public ScenarioKind Kind { get; }
            public string Label { get; }
            public string Path { get; }
            public float Timeout { get; }
            public int MaxRetry { get; }
            public float RetryDelay { get; }
            public string Expectation { get; }
            public int ExpectedAttempts => MaxRetry + 1;
        }

        private sealed class RunState
        {
            public readonly List<CallbackRecord> Callbacks = new();
            public int BaseCacheHits;
            public int BaseMerged;
            public int BaseRequests;
            public int BaseStarts;
            public bool[] CallbackDone;
            public bool Cancelled;
            public int Completed;
            public float ElapsedMs;
            public int Failed;
            public bool Finalized;
            public float FinalizedRealtime;
            public int Issued;
            public Texture2D Preview;
            public int RunCacheHits;
            public int RunId;
            public int RunMerged;
            public int RunRequests;
            public int RunStarts;
            public Scenario Scenario;
            public ServerSnapshot Server;
            public float StartedRealtime;
            public int Success;
            public string Url;
            public string Verdict;
        }

        private struct CallbackRecord
        {
            public float ElapsedMs;
            public int Frame;
            public int Index;
            public bool Success;
            public int TextureId;
        }
    }
}
