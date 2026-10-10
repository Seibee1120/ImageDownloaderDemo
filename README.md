# ImageDownloader (Unity)

A async remote-image loader for Unity.

Drop the `Runtime/` folder into any Unity project (2020.3 LTS or newer) and you
get a dependency-free image downloading service with request de-duplication, an LRU +
memory-budgeted texture cache, reference counting, owner lifetime binding, bounded concurrency,
timeout, retry and cancellation.

> Demo is included: open `Demo/`, attach `ImageDownloaderDemo` to any GameObject and press Play.
> The controller spawns **two independent owner galleries** and keeps a live cache/download HUD.
> Both galleries draw from one shared image pool with **deliberately overlapping index ranges**, so a
> band of URLs is requested by *both* owners at once. That single arrangement exercises the three
> headline behaviours at a glance:
>
> - **Request de-duplication** — the shared URLs are downloaded once and multicast to both galleries.
> - **Cross-owner reference counting** — a shared texture is retained by *both* owners (refcount 2);
>   pressing **Destroy A / Destroy B** releases only that gallery's references, so the images still
>   shown by the surviving gallery stay cached and are never evicted.
> - **Owner lifetime binding** — destroying an owner drops its in-flight callbacks; the HUD stays
>   alive so the effect is visible.

## Features

| Area | What it does |
| --- | --- |
| Request de-duplication | Concurrent requests for the same URL are merged into a single download and all callers are notified when it finishes. |
| LRU cache | Least-recently-used eviction driven by both a **count limit** and an approximate **memory budget**. |
| Reference counting | Entries still in use by a live owner are pinned and never evicted; released entries become evictable again. |
| Owner lifetime binding | `GetTexture/GetSprite(url, owner, ...)` attaches a hidden tracker to the owner. When the owner is destroyed, its pending callbacks are dropped and its cache refs released automatically. |
| Concurrency control | At most `maxConcurrentDownloads` transfers run at once; the rest wait in a FIFO queue and are pumped as slots free up. |
| Robustness | Per-request timeout (Unity timeout **plus** a manual unscaled-time fallback), retry with delay, and per-URL / global cancellation. |
| URL normalization | Lowercased scheme/host, default-port and fragment stripping, trailing-slash trimming. Query sorting is **opt-in** so signed URLs are not corrupted. |
| Sprite caching | Sprites are created lazily per `pixelsPerUnit` and share the cached texture; every generated `Texture2D`/`Sprite` is destroyed on eviction. |

## Quick start

```csharp
using ImageDownloaderDemo;

// Fire-and-forget with owner lifetime binding.
ImageDownloader.Instance.GetSprite(url, this, sprite =>
{
    if (sprite != null) image.sprite = sprite;
});

// Textures, with an explicit refresh that bypasses the cache.
ImageDownloader.Instance.GetTexture(url, this, tex => rawImage.texture = tex, forceRefresh: true);

// Cancel / inspect.
ImageDownloader.Instance.Cancel(url);
Debug.Log(ImageDownloader.Instance.CacheCount);
```

Parameters (`timeoutSeconds`, `maxRetry`, `retryDelaySeconds`, `maxConcurrentDownloads`,
`maxCacheCount`, `maxCacheMemoryBytes`, `sortQueryParameters`) are public fields, editable in the
Inspector once the component exists.

## Layout

Two assemblies: the reusable runtime carries the logic and has **no references**; the demo
depends on it, never the other way around.

```
Assets/
  Runtime/                              # reusable runtime assembly
    ImageDownloader.cs                  # the loader
    MonoSingleton.cs                    # minimal self-contained singleton base
    ImageDownloaderDemo.Runtime.asmdef  # assembly: ImageDownloaderDemo.Runtime | namespace: ImageDownloaderDemo | references: []
  Demo/                                 # sample assembly
    ImageDownloaderDemo.cs              # zero-setup IMGUI controller (toolbar + stats + two galleries)
    ImageDownloaderOwnerPanel.cs        # independent owner that loads one index range of the shared pool
    ImageDownloaderDemo.Demo.asmdef     # assembly: ImageDownloaderDemo.Demo | namespace: ImageDownloaderDemo.Demo | references: [ImageDownloaderDemo.Runtime]
  README.md
```

The original component inherited a project-wide singleton that pulled in an event bus; the demo
ships a tiny `MonoSingleton<T>` so the package has **no external dependencies**.

> The runtime is its own assembly (`ImageDownloaderDemo.Runtime`) instead of living in
> `Assembly-CSharp`, so the "drop-in, dependency-free" boundary is enforced by the compiler rather
> than by convention. The demo assembly references it one-way. No further splitting is warranted at
> this size.

## Design notes

- **Single entry point.** All requests funnel through `Request`, which resolves in a fixed order:
  bind owner → cache hit (synchronous callback) → force-refresh eviction → merge/create download.
- **Cache hit is synchronous.** This keeps callers simple but means the callback may run before
  `GetSprite` returns — do not rely on it running on a later frame.
- **Memory estimate is approximate.** `EstimateTextureBytes` uses the real texture format and adds
  ~1/3 for a mip chain. It is a budgeting heuristic, not an exact allocation size.
- **Oversized single texture.** `EvictIfNeeded(protectUrl)` protects the entry just inserted, so a
  texture larger than the whole budget is kept rather than evicted into a destroyed handle.
- **Pinned entries.** If live owners pin more memory than the budget, eviction stops and the budget
  is temporarily exceeded until refs are released. This is intentional.

## Possible extensions

- Persistent **disk cache** for cross-session reuse.
- Extract the cache/queue/ref-count logic into a plain-C# core and add EditMode unit tests.
- Report quantitative results (request de-duplication ratio, hit rate, memory headroom) in the README.

## 中文说明

这是从某商业 Unity 手游项目中抽离、并做了 clean-room 重写的异步图片加载组件，用于个人作品集：

- 同一 URL 的并发请求会**合并为一次下载**；
- 纹理缓存同时受**数量**与**内存预算**约束，采用 **LRU** 淘汰，并用**引用计数**保护正在使用的资源；
- 通过 owner 生命周期绑定，对象销毁时自动**丢弃回调、释放引用**；
- 支持**并发上限、超时、重试、取消**；
- `Demo/` 下附零配置的可视化示例（挂上组件即可运行）：控制器会创建**两个画廊**，它们从同一图片池取图且**索引区间刻意重叠**，于是有一批 URL 被两个 owner 同时请求——可直观看到**去重**（只下载一次）、**跨 owner 引用计数**（单独销毁一个画廊只释放它的引用，共享纹理仍被另一个画廊引用而保留）与 **owner 销毁时丢弃回调/释放引用**。

依赖：仅 Unity（2020.3 LTS+），无第三方包。
