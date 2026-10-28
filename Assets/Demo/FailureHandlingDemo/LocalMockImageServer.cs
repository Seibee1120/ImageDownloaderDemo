using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ImageDownloaderDemo.Demo.FailureHandlingDemo
{
    /// <summary>
    ///     <see cref="FailureHandlingDemo" /> 使用的极简 loopback HTTP/1.1 服务器。
    ///     它故意“行为不端”（延迟、5xx/4xx、声明 image/png 却返回非图片数据），
    ///     让 timeout / retry / decode-failure / HTTP-failure 分支可以<strong>离线、确定性</strong>地复现。
    ///     只绑定 127.0.0.1，不访问外网。
    /// </summary>
    internal sealed class LocalMockImageServer : IDisposable
    {
        private const string GarbageBody = "this is not a png; the server lied about Content-Type";

        private readonly Thread _acceptThread;
        private readonly List<AttemptLogEntry> _entries = new();
        private readonly object _gate = new();
        private readonly TcpListener _listener;
        private readonly byte[] _pngBytes;

        private int _attempts;
        private bool _clientAbortSeen;
        private int _epoch;
        private int _flakySeen;
        private long _runStartTicks;
        private volatile bool _running = true;

        public LocalMockImageServer(byte[] pngBytes)
        {
            _pngBytes = pngBytes ?? Array.Empty<byte>();

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _runStartTicks = Stopwatch.GetTimestamp();
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "ImageDownloaderMockServer"
            };
            _acceptThread.Start();
        }

        /// <summary>系统分配的 loopback 端口。</summary>
        public int Port { get; }

        public bool IsRunning => _running;

        /// <summary>当前 run 已收到的请求数（服务器视角，可能包含已被客户端中止的尝试）。</summary>
        public int Attempts
        {
            get
            {
                lock (_gate) return _attempts;
            }
        }

        public void Dispose()
        {
            if (!_running) return;
            _running = false;

            try
            {
                _listener.Stop();
            }
            catch (SocketException)
            {
                // 已经停止。
            }
            catch (ObjectDisposedException)
            {
                // 已经停止。
            }
        }

        /// <summary>开始新一轮实验：清空计数器与日志，并重置每次尝试的相对时间。</summary>
        public void BeginRun()
        {
            lock (_gate)
            {
                _epoch++;
                _runStartTicks = Stopwatch.GetTimestamp();
                _entries.Clear();
                _attempts = 0;
                _flakySeen = 0;
                _clientAbortSeen = false;
            }
        }

        public ServerSnapshot Snapshot()
        {
            lock (_gate)
            {
                var snapshot = new ServerSnapshot
                {
                    Attempts = _attempts,
                    ClientAbortSeen = _clientAbortSeen,
                    Entries = _entries.ToArray()
                };

                foreach (var entry in snapshot.Entries)
                {
                    if (entry.Aborted) snapshot.Aborted++;
                    else if (entry.Status >= 500) snapshot.Status5xx++;
                    else if (entry.Status >= 400) snapshot.Status4xx++;
                    else if (entry.Status >= 200) snapshot.Status2xx++;
                }

                return snapshot;
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    if (!_running) return;
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                // 每条连接独立处理：慢请求不能阻塞后续的重试连接。
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
        }

        private void HandleClient(TcpClient client)
        {
            var path = "(unparsed)";
            var epoch = 0;
            var entryIndex = -1;

            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;

                    var stream = client.GetStream();
                    var requestLine = ReadRequestLine(stream);
                    if (string.IsNullOrEmpty(requestLine)) return;

                    var parts = requestLine.Split(' ');
                    if (parts.Length < 2) return;

                    var target = parts[1];
                    var queryIndex = target.IndexOf('?');
                    path = queryIndex >= 0 ? target.Substring(0, queryIndex) : target;
                    var query = queryIndex >= 0 ? target.Substring(queryIndex + 1) : string.Empty;

                    lock (_gate) epoch = _epoch;

                    // 在收到请求时就把 attempt 计入：即使客户端随后超时断开，
                    // 也能保证本次尝试出现在计数与日志里。
                    entryIndex = BeginAttempt(epoch, path);
                    if (entryIndex < 0) return;

                    switch (path)
                    {
                        case "/ok":
                            CompleteAttempt(epoch, entryIndex, 200, "200 image/png  (valid PNG)");
                            Send(stream, 200, "image/png", _pngBytes);
                            break;

                        case "/http-500":
                            CompleteAttempt(epoch, entryIndex, 500, "HTTP 500  (server error)");
                            Send(stream, 500, "text/plain; charset=utf-8",
                                Encoding.UTF8.GetBytes("mock internal error"));
                            break;

                        case "/http-404":
                            CompleteAttempt(epoch, entryIndex, 404, "HTTP 404  (not found)");
                            Send(stream, 404, "text/plain; charset=utf-8",
                                Encoding.UTF8.GetBytes("mock not found"));
                            break;

                        case "/flaky":
                        {
                            var failFirst = ParseInt(query, "fail", 2);
                            int seen;
                            bool fail;

                            lock (_gate)
                            {
                                if (epoch != _epoch) return;
                                seen = ++_flakySeen;
                                fail = seen <= failFirst;
                            }

                            if (fail)
                            {
                                CompleteAttempt(epoch, entryIndex, 500,
                                    $"HTTP 500  (transient failure {seen}/{failFirst})");
                                Send(stream, 500, "text/plain; charset=utf-8",
                                    Encoding.UTF8.GetBytes($"mock transient failure {seen}/{failFirst}"));
                            }
                            else
                            {
                                CompleteAttempt(epoch, entryIndex, 200,
                                    "200 image/png  (valid PNG after retry)");
                                Send(stream, 200, "image/png", _pngBytes);
                            }

                            break;
                        }

                        case "/garbage":
                            // 关键：HTTP 200 + image/png，但响应体根本不是图片 -> 解码失败。
                            CompleteAttempt(epoch, entryIndex, 200,
                                "200 image/png  (body is NOT a PNG -> decode failure)");
                            Send(stream, 200, "image/png", Encoding.UTF8.GetBytes(GarbageBody));
                            break;

                        case "/slow":
                        {
                            var delayMs = Math.Max(250, ParseInt(query, "ms", 5000));
                            var elapsed = WaitWhileClientAlive(delayMs, client, out var aborted);

                            if (!_running) return;

                            if (aborted)
                            {
                                CompleteAttempt(epoch, entryIndex, 0,
                                    $"no response; client aborted after {elapsed:F0} ms (timeout)", true);
                                return;
                            }

                            CompleteAttempt(epoch, entryIndex, 200,
                                $"200 image/png  (sent after {delayMs} ms)");
                            Send(stream, 200, "image/png", _pngBytes);
                            break;
                        }

                        default:
                            CompleteAttempt(epoch, entryIndex, 404, "HTTP 404  (unknown mock route)");
                            Send(stream, 404, "text/plain; charset=utf-8",
                                Encoding.UTF8.GetBytes("unknown mock route"));
                            break;
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException)
            {
                // 客户端主动断开（UnityWebRequest.Abort / 取消）会走到这里。
                CompleteAttempt(epoch, entryIndex, 0,
                    "client closed the connection (timeout / cancel)", true);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[MockImageServer] {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        ///     模拟一个迟迟不返回的服务器，同时用 Poll 检测客户端是否已经提前断开。
        ///     这样超时不仅能在客户端观察到，也能在服务器日志里留下“客户端在等待 N ms 后断开”的证据。
        /// </summary>
        private double WaitWhileClientAlive(int delayMs, TcpClient client, out bool aborted)
        {
            aborted = false;
            var start = Stopwatch.GetTimestamp();

            while (_running)
            {
                var elapsed = ElapsedMs(start);
                if (elapsed >= delayMs) return elapsed;

                try
                {
                    if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0)
                    {
                        aborted = true;
                        return elapsed;
                    }
                }
                catch (SocketException)
                {
                    aborted = true;
                    return elapsed;
                }
                catch (ObjectDisposedException)
                {
                    aborted = true;
                    return elapsed;
                }

                Thread.Sleep(20);
            }

            return ElapsedMs(start);
        }

        private int BeginAttempt(int epoch, string path)
        {
            lock (_gate)
            {
                if (epoch != _epoch) return -1;

                _attempts++;
                _entries.Add(new AttemptLogEntry
                {
                    StartedMs = ElapsedMs(_runStartTicks),
                    Path = path,
                    Status = 0,
                    Outcome = "waiting for response…",
                    Aborted = false
                });

                return _entries.Count - 1;
            }
        }

        private void CompleteAttempt(int epoch, int entryIndex, int status, string outcome, bool aborted = false)
        {
            lock (_gate)
            {
                if (epoch != _epoch || entryIndex < 0 || entryIndex >= _entries.Count) return;

                var entry = _entries[entryIndex];
                entry.Status = status;
                entry.Outcome = outcome;
                entry.Aborted = aborted;
                _entries[entryIndex] = entry;

                if (aborted) _clientAbortSeen = true;
            }
        }

        private static string ReadRequestLine(NetworkStream stream)
        {
            var buffer = new byte[2048];
            var length = 0;

            while (length < buffer.Length)
            {
                var value = stream.ReadByte();
                if (value < 0) return null;

                buffer[length++] = (byte)value;
                if (length >= 4 &&
                    buffer[length - 4] == (byte)'\r' && buffer[length - 3] == (byte)'\n' &&
                    buffer[length - 2] == (byte)'\r' && buffer[length - 1] == (byte)'\n')
                    break;
            }

            var text = Encoding.ASCII.GetString(buffer, 0, length);
            var firstLine = text.Split('\n')[0].TrimEnd('\r');
            return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine;
        }

        private static void Send(NetworkStream stream, int status, string contentType, byte[] body)
        {
            var header = string.Concat(
                "HTTP/1.1 ", status, " ", ReasonPhrase(status), "\r\n",
                "Content-Type: ", contentType, "\r\n",
                "Content-Length: ", body.Length, "\r\n",
                "Cache-Control: no-store\r\n",
                "Connection: close\r\n\r\n");

            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 404: return "Not Found";
                case 500: return "Internal Server Error";
                default: return "Mock";
            }
        }

        private static int ParseInt(string query, string key, int fallback)
        {
            if (string.IsNullOrEmpty(query)) return fallback;

            foreach (var pair in query.Split('&'))
            {
                var equals = pair.IndexOf('=');
                if (equals <= 0) continue;
                if (!string.Equals(pair.Substring(0, equals), key, StringComparison.OrdinalIgnoreCase)) continue;
                return int.TryParse(pair.Substring(equals + 1), out var value) ? value : fallback;
            }

            return fallback;
        }

        private static double ElapsedMs(long startTicks)
        {
            return (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
        }
    }

    internal struct AttemptLogEntry
    {
        public bool Aborted;
        public string Outcome;
        public string Path;
        public double StartedMs;
        public int Status;
    }

    internal sealed class ServerSnapshot
    {
        public int Aborted;
        public int Attempts;
        public bool ClientAbortSeen;
        public AttemptLogEntry[] Entries = Array.Empty<AttemptLogEntry>();
        public int Status2xx;
        public int Status4xx;
        public int Status5xx;
    }
}
