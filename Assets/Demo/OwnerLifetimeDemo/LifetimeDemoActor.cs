using System;
using UnityEngine;

namespace ImageDownloaderDemo.Demo.OwnerLifetimeDemo
{
    /// <summary>
    ///     Demo 中发起请求的 GameObject。
    ///     绑定 owner 时，<see cref="ImageDownloader" /> 会在本 GameObject 上挂一个隐藏的
    ///     <c>TextureRefTracker</c>：销毁 GameObject 会触发 tracker 的 OnDestroy，从而把本 owner
    ///     从所有进行中的请求里移除（回调被丢弃），并释放它持有的缓存引用。
    ///     不绑定 owner 时，本组件只是一个用于对照的“发起者”，销毁它不会影响已经交出去的委托。
    /// </summary>
    [AddComponentMenu("ImageDownloader Demo/Lifetime Demo Actor")]
    public class LifetimeDemoActor : MonoBehaviour
    {
        public string Label { get; private set; } = "Actor";
        public bool BoundToLifecycle { get; private set; }

        /// <summary>本组件销毁时触发，Demo 控制器用它更新对应的请求记录。</summary>
        public event Action<LifetimeDemoActor> Destroyed;

        public void Configure(string label, bool boundToLifecycle)
        {
            Label = label;
            BoundToLifecycle = boundToLifecycle;
            gameObject.name = boundToLifecycle ? $"LifetimeOwner_{label}" : $"LifetimeRequester_{label}";
        }

        private void OnDestroy()
        {
            Destroyed?.Invoke(this);

            Debug.Log($"[LifetimeDemo] '{Label}' 被销毁 " +
                      (BoundToLifecycle
                          ? "（绑定了 owner：进行中的回调会被丢弃，缓存引用会被释放）。"
                          : "（未绑定 owner：下载器持有回调委托，回调仍会送达）。"));
        }
    }
}
