using System.Collections.Generic;
using UnityEngine;

namespace ImageDownloaderDemo.Demo
{
    /// <summary>
    ///     零配置演示控制器：将此组件挂到任意 GameObject 上并按 Play 运行即可。
    /// </summary>
    [AddComponentMenu("ImageDownloader Demo/Image Downloader Demo")]
    public class ImageDownloaderDemo : MonoBehaviour
    {
        [SerializeField] private string poolSeed = "imagedownloader";
        [SerializeField] private int galleryAStart;
        [SerializeField] private int galleryACount = 30;
        [SerializeField] private int galleryBStart = 20;
        [SerializeField] private int galleryBCount = 30;
        [SerializeField] private float thumbnailSize = 96f;
        [SerializeField] private bool spawnOwnerOnStart = true;

        private ImageDownloaderOwnerPanel _panelA;
        private ImageDownloaderOwnerPanel _panelB;

        // ----- shared-pool math --------------------------------------------

        private int AEnd => galleryAStart + galleryACount;
        private int BEnd => galleryBStart + galleryBCount;

        private void Start()
        {
            if (spawnOwnerOnStart) SpawnGalleries();
        }

        private void OnGUI()
        {
            var downloader = ImageDownloader.HasInstance ? ImageDownloader.Instance : null;

            GUILayout.BeginVertical();
            DrawToolbar(downloader);

            GUILayout.BeginHorizontal();
            DrawPanel(_panelA, "Gallery A");
            DrawPanel(_panelB, "Gallery B");
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        // ----- layout -------------------------------------------------------

        private void DrawPanel(ImageDownloaderOwnerPanel panel, string label)
        {
            var width = Mathf.Max(260f, Screen.width * 0.5f - 24f);
            var height = Mathf.Max(220f, Screen.height - 130f);

            if (panel != null)
            {
                panel.DrawGallery(width, height);
                return;
            }

            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(width), GUILayout.Height(height));
            GUILayout.Label($"{label} 已销毁。点 “Spawn Galleries” 重新创建。");
            GUILayout.Label("观察：被销毁画廊独占的纹理引用被释放；与另一个画廊共享的纹理仍被引用而保留在缓存中。");
            GUILayout.EndVertical();
        }

        // ----- spawn / destroy ---------------------------------------------

        private void SpawnGalleries()
        {
            if (_panelA == null) _panelA = CreatePanel("Gallery A", "Owner Panel A", galleryAStart, galleryACount);
            if (_panelB == null) _panelB = CreatePanel("Gallery B", "Owner Panel B", galleryBStart, galleryBCount);
        }

        private ImageDownloaderOwnerPanel CreatePanel(string title, string goName, int start, int count)
        {
            var go = new GameObject(goName);
            go.SetActive(false); // configure before OnEnable so the range is applied on activation

            var panel = go.AddComponent<ImageDownloaderOwnerPanel>();
            panel.Configure(title, poolSeed, start, count, thumbnailSize, SharedUrls());

            go.SetActive(true);
            return panel;
        }

        private void DestroyGalleries()
        {
            DestroyPanel(ref _panelA);
            DestroyPanel(ref _panelB);
        }

        private void DestroyPanel(ref ImageDownloaderOwnerPanel panel)
        {
            if (panel == null) return;
            Destroy(panel.gameObject);
            panel = null;
        }

        private void DownloadAll(bool forceRefresh)
        {
            if (_panelA != null) _panelA.DownloadAll(forceRefresh);
            if (_panelB != null) _panelB.DownloadAll(forceRefresh);
        }

        // ----- toolbar ------------------------------------------------------

        private void DrawToolbar(ImageDownloader downloader)
        {
            var anyAlive = _panelA != null || _panelB != null;

            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Spawn Galleries", GUILayout.Width(120))) SpawnGalleries();

            GUI.enabled = anyAlive;
            if (GUILayout.Button("Destroy Galleries", GUILayout.Width(130))) DestroyGalleries();
            if (GUILayout.Button("Reload (cache)", GUILayout.Width(120))) DownloadAll(false);
            if (GUILayout.Button("Refresh (force)", GUILayout.Width(120))) DownloadAll(true);
            GUI.enabled = true;

            GUI.enabled = _panelA != null;
            if (GUILayout.Button("Destroy A", GUILayout.Width(85))) DestroyPanel(ref _panelA);
            GUI.enabled = _panelB != null;
            if (GUILayout.Button("Destroy B", GUILayout.Width(85))) DestroyPanel(ref _panelB);
            GUI.enabled = true;

            if (GUILayout.Button("Clear Cache", GUILayout.Width(95)))
            {
                downloader?.ClearCache();
                if (_panelA != null) _panelA.ClearLocalView();
                if (_panelB != null) _panelB.ClearLocalView();
            }

            if (GUILayout.Button("Cancel All", GUILayout.Width(95)))
                downloader?.CancelAll();

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (downloader != null)
            {
                var memMb = downloader.CacheMemoryBytes / (1024f * 1024f);
                GUILayout.Label(
                    $"distinct={UnionCount()} (A={galleryACount} + B={galleryBCount}, 共享 {SharedCount()})   " +
                    $"cache={downloader.CacheCount}   active={downloader.ActiveDownloadCount}   " +
                    $"downloading={downloader.DownloadingCount}   mem≈{memMb:F1}MB");
            }

            GUILayout.EndVertical();
        }

        private int SharedCount()
        {
            var loA = Mathf.Min(galleryAStart, AEnd);
            var hiA = Mathf.Max(galleryAStart, AEnd);
            var loB = Mathf.Min(galleryBStart, BEnd);
            var hiB = Mathf.Max(galleryBStart, BEnd);
            return Mathf.Max(0, Mathf.Min(hiA, hiB) - Mathf.Max(loA, loB));
        }

        private int UnionCount()
        {
            var loA = Mathf.Min(galleryAStart, AEnd);
            var hiA = Mathf.Max(galleryAStart, AEnd);
            var loB = Mathf.Min(galleryBStart, BEnd);
            var hiB = Mathf.Max(galleryBStart, BEnd);
            return Mathf.Max(hiA, hiB) - Mathf.Min(loA, loB);
        }

        /// <summary>
        ///     URLs that appear in both galleries, i.e. the intersection of the two index ranges.
        /// </summary>
        private IEnumerable<string> SharedUrls()
        {
            var lo = Mathf.Max(galleryAStart, galleryBStart);
            var hi = Mathf.Min(AEnd, BEnd);
            var urls = new List<string>();
            for (var i = lo; i < hi; i++)
                urls.Add(UrlFor(i));
            return urls;
        }

        private string UrlFor(int index)
        {
            return $"https://picsum.photos/seed/{poolSeed}{index}/256";
        }
    }
}