using UnityEngine;

namespace ImageDownloaderDemo.Demo.OwnerRefCountDemo
{
    /// <summary>
    ///     跨 owner 引用计数 Demo 中的一个独立 owner。
    ///     把本组件作为 <c>owner</c> 传给 <see cref="ImageDownloader" /> 后，运行时会在同一个
    ///     GameObject 上挂一个隐藏的 <c>TextureRefTracker</c>：每发起一次请求就为 URL 增加一个
    ///     缓存引用，而销毁这个 GameObject 时 tracker 只释放它自己持有的那一份。
    /// </summary>
    [AddComponentMenu("ImageDownloader Demo/Ref Count Owner")]
    public class RefCountOwner : MonoBehaviour
    {
        public string OwnerLabel { get; private set; } = "Owner";
        public string Url { get; private set; }
        public Texture2D Texture { get; private set; }
        public bool Completed { get; private set; }
        public bool Failed { get; private set; }
        public int CallbackFrame { get; private set; }
        public string Status { get; private set; } = "not requested";

        public void Configure(string label)
        {
            OwnerLabel = label;
            gameObject.name = $"RefCountOwner_{label}";
        }

        /// <summary>
        ///     请求共享 URL。每次调用都会新增一个缓存引用（cache hit 也会）。
        /// </summary>
        public void RequestShared(string url, bool forceRefresh = false)
        {
            Url = url;
            Texture = null;
            Completed = false;
            Failed = false;
            Status = "loading...";

            ImageDownloader.Instance.GetTexture(url, this, texture =>
            {
                Texture = texture;
                Completed = true;
                Failed = texture == null;
                CallbackFrame = Time.frameCount;
                Status = Failed ? "failed/cancelled" : "ok";
            }, forceRefresh);
        }

        private void OnDestroy()
        {
            Debug.Log($"[RefCountOwner] '{OwnerLabel}' 被销毁：它的 TextureRefTracker 会释放自己持有的缓存引用。");
        }
    }
}
