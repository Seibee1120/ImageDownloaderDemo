using System.Collections.Generic;
using UnityEngine;

namespace ImageDownloaderDemo.Demo
{
    /// <summary>
    /// Zero-setup demo: attach this component to any GameObject and press Play.
    /// It downloads a list of remote images through <see cref="ImageDownloader"/> and draws them
    /// with IMGUI, so no UI package or scene wiring is required.
    ///
    /// The component passes itself as the <c>owner</c> of every request, which demonstrates the
    /// lifetime binding: destroy this GameObject and all in-flight callbacks are dropped and the
    /// cache references released automatically.
    /// </summary>
    [AddComponentMenu("ImageDownloader Demo/Image Downloader Demo")]
    public class ImageDownloaderDemo : MonoBehaviour
    {
        [Tooltip("Number of sample images to load.")]
        [SerializeField] private int imageCount = 40;

        [Tooltip("Seed used to build the sample picsum.photos URLs.")]
        [SerializeField] private string urlSeed = "imagedownloader";

        [Tooltip("Thumbnail edge length in pixels.")]
        [SerializeField] private float thumbnailSize = 96f;

        private string[] _urls;
        private readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
        private readonly Dictionary<string, string> _status = new Dictionary<string, string>();
        private Vector2 _scroll;

        private void OnEnable()
        {
            EnsureUrls();
            DownloadAll(forceRefresh: false);
        }

        private void EnsureUrls()
        {
            if (_urls != null && _urls.Length == imageCount) return;

            _urls = new string[imageCount];
            for (var i = 0; i < imageCount; i++)
                _urls[i] = $"https://picsum.photos/seed/{urlSeed}{i}/256";
        }

        private void DownloadAll(bool forceRefresh)
        {
            EnsureUrls();
            var downloader = ImageDownloader.Instance;

            _sprites.Clear();
            foreach (var url in _urls)
            {
                _status[url] = "loading...";
                var captured = url;
                downloader.GetSprite(captured, this, sprite =>
                {
                    _sprites[captured] = sprite;
                    _status[captured] = sprite != null ? "ok" : "failed/cancelled";
                }, 100f, forceRefresh);
            }
        }

        private void ClearLocalView()
        {
            _sprites.Clear();
            foreach (var url in _urls) _status[url] = "cleared";
        }

        private void OnGUI()
        {
            var downloader = ImageDownloader.HasInstance ? ImageDownloader.Instance : null;

            GUILayout.BeginVertical();
            DrawToolbar(downloader);

            _scroll = GUILayout.BeginScrollView(_scroll);
            foreach (var url in _urls)
                DrawRow(url);
            GUILayout.EndScrollView();

            GUILayout.EndVertical();
        }

        private void DrawToolbar(ImageDownloader downloader)
        {
            GUILayout.BeginHorizontal(GUI.skin.box);

            if (GUILayout.Button("Reload (cache)", GUILayout.Width(130)))
                DownloadAll(forceRefresh: false);

            if (GUILayout.Button("Refresh (force)", GUILayout.Width(130)))
                DownloadAll(forceRefresh: true);

            if (GUILayout.Button("Clear Cache", GUILayout.Width(100)))
            {
                downloader?.ClearCache();
                ClearLocalView();
            }

            if (GUILayout.Button("Cancel All", GUILayout.Width(100)))
                downloader?.CancelAll();

            GUILayout.FlexibleSpace();

            if (downloader != null)
            {
                var memMb = downloader.CacheMemoryBytes / (1024f * 1024f);
                GUILayout.Label(
                    $"cache={downloader.CacheCount}  active={downloader.ActiveDownloadCount}  " +
                    $"downloading={downloader.DownloadingCount}  mem≈{memMb:F1}MB",
                    GUILayout.Width(420));
            }

            GUILayout.EndHorizontal();
        }

        private void DrawRow(string url)
        {
            GUILayout.BeginHorizontal(GUI.skin.box);

            var rect = GUILayoutUtility.GetRect(thumbnailSize, thumbnailSize, GUILayout.Width(thumbnailSize),
                GUILayout.Height(thumbnailSize));

            if (_sprites.TryGetValue(url, out var sprite) && sprite != null && sprite.texture != null)
                GUI.DrawTexture(rect, sprite.texture, ScaleMode.ScaleToFit);
            else
                GUI.Box(rect, "…");

            GUILayout.BeginVertical();
            GUILayout.Label(_status.TryGetValue(url, out var status) ? status : "-");
            GUILayout.Label(url);
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }
    }
}
