using System.Collections.Generic;
using UnityEngine;

namespace ImageDownloaderDemo.Demo
{
    /// <summary>
    ///     独立的 owner（消费者）：向 <see cref="ImageDownloader" /> 发起图片请求，并把自身作为
    ///     <c>owner</c> 传入，从而把请求与缓存引用绑定到本对象的生命周期上。
    /// </summary>
    public class ImageDownloaderOwnerPanel : MonoBehaviour
    {
        private static GUIStyle _headerStyle;
        private static GUIStyle _urlStyle;

        [SerializeField] private string title = "Gallery";
        [SerializeField] private string poolSeed = "imagedownloader";
        [SerializeField] private int startIndex;
        [SerializeField] private int imageCount = 30;
        [SerializeField] private float thumbnailSize = 96f;

        private readonly HashSet<string> _sharedUrls = new();
        private readonly Dictionary<string, Sprite> _sprites = new();
        private readonly Dictionary<string, string> _status = new();
        private float _contentWidth = 260f;
        private Vector2 _scroll;
        private string[] _urls;
        private string _urlsSeed;
        private int _urlsStart;

        public string Title => title;

        private static GUIStyle HeaderStyle
        {
            get
            {
                if (_headerStyle != null) return _headerStyle;
                _headerStyle = new GUIStyle(GUI.skin.label) { richText = true };
                return _headerStyle;
            }
        }

        private static GUIStyle UrlStyle
        {
            get
            {
                if (_urlStyle != null) return _urlStyle;
                _urlStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
                return _urlStyle;
            }
        }

        private void OnEnable()
        {
            EnsureUrls();
            DownloadAll(false);
        }

        private void OnDestroy()
        {
            Debug.Log($"[OwnerPanel] '{title}' 被销毁：其进行中的回调将被丢弃，缓存引用将被释放。");
        }

        /// <summary>
        ///     供 Demo 控制器在运行时配置面板。必须在面板被激活（<c>OnEnable</c>）之前调用，
        /// </summary>
        /// <param name="sharedUrls">出现在其它画廊中的 URL 集合，仅用于在界面上标记“共享”。</param>
        public void Configure(string galleryTitle, string seed, int start, int count, float size,
            IEnumerable<string> sharedUrls = null)
        {
            title = galleryTitle;
            poolSeed = seed;
            startIndex = start;
            imageCount = count;
            thumbnailSize = size;
            _urls = null; // force a rebuild from the new range

            _sharedUrls.Clear();
            if (sharedUrls == null) return;
            foreach (var url in sharedUrls) _sharedUrls.Add(url);
        }

        private void EnsureUrls()
        {
            var count = Mathf.Max(0, imageCount);
            if (_urls != null && _urls.Length == count && _urlsStart == startIndex && _urlsSeed == poolSeed)
                return;

            _urlsStart = startIndex;
            _urlsSeed = poolSeed;
            _urls = new string[count];
            for (var i = 0; i < count; i++)
                _urls[i] = UrlFor(startIndex + i);
        }

        private string UrlFor(int index)
        {
            return $"https://picsum.photos/seed/{poolSeed}{index}/256";
        }

        /// <summary>
        ///     下载全部图片，并以 <c>this</c> 作为 owner 绑定生命周期。
        /// </summary>
        public void DownloadAll(bool forceRefresh)
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

        public void ClearLocalView()
        {
            _sprites.Clear();
            if (_urls == null) return;
            foreach (var url in _urls) _status[url] = "cleared";
        }

        /// <summary>
        ///     绘制画廊。由控制器在一个固定大小的列中调用。
        /// </summary>
        public void DrawGallery(float width, float height)
        {
            EnsureUrls();

            var sharedCount = 0;
            foreach (var url in _urls)
                if (_sharedUrls.Contains(url))
                    sharedCount++;

            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(width), GUILayout.Height(height));

            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{title}</b>   {_urls.Length} 张", HeaderStyle);
            GUILayout.FlexibleSpace();
            if (sharedCount > 0)
                GUILayout.Label($"[共享 {sharedCount}]", GUI.skin.box);
            GUILayout.EndHorizontal();

            GUILayout.Space(2);
            _contentWidth = Mathf.Max(120f, width - thumbnailSize - 32f);
            _scroll = GUILayout.BeginScrollView(_scroll);
            foreach (var url in _urls)
                DrawRow(url);
            GUILayout.EndScrollView();

            GUILayout.EndVertical();
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

            GUILayout.BeginHorizontal();
            GUILayout.Label(_status.TryGetValue(url, out var status) ? status : "-");
            if (_sharedUrls.Contains(url))
                GUILayout.Label("[共享]", GUI.skin.box);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label(url, UrlStyle, GUILayout.Width(_contentWidth));

            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }
    }
}