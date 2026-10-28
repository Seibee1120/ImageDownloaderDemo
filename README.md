# ImageDownloader（Unity）

面向 Unity 的异步远程图片加载组件：把 `Assets/Runtime/` 复制进任意工程即可使用，**无第三方依赖**。核心解决大量 UI 图片并发加载时的重复下载、缓存失控、对象销毁后的悬空回调与资源泄漏问题。

## 快速开始

```csharp
using ImageDownloaderDemo;

// 带 owner 生命周期绑定：owner 销毁后回调自动失效、引用自动释放
ImageDownloader.Instance.GetSprite(url, this, sprite =>
{
    if (sprite != null) image.sprite = sprite;
});

// 加载纹理，并绕过缓存强制刷新
ImageDownloader.Instance.GetTexture(url, this, tex => rawImage.texture = tex, forceRefresh: true);

// 取消单个 URL / 查询状态
ImageDownloader.Instance.Cancel(url);
Debug.Log(ImageDownloader.Instance.CacheCount);
```

不传 owner 的重载需要调用方自行配对 `RetainCacheRef` / `ReleaseCacheRef`。

主要参数均为 `public` 字段，组件创建后可在 Inspector 中调整：

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `timeoutSeconds` | `30` | 单次请求超时；`<= 0` 关闭手动超时 |
| `maxRetry` | `2` | 首次失败后的重试次数，总尝试次数为 `1 + maxRetry` |
| `retryDelaySeconds` | `0.5` | 重试间隔（realtime） |
| `maxConcurrentDownloads` | `8` | 最大并发传输数，最小为 1 |
| `maxCacheCount` | `50` | 缓存纹理数量上限；`<= 0` 不限制 |
| `maxCacheMemoryBytes` | `100 MB` | 缓存内存预算（估算值）；`<= 0` 不限制 |
| `sortQueryParameters` | `false` | 是否排序 query，签名 URL 应保持关闭 |

## 演示场景

全部场景均为零配置：打开场景直接 Play；或把对应组件挂到任意 GameObject 上运行。

| 场景 | 入口 | 观察点 |
| --- | --- | --- |
| 综合 Demo | `Assets/Demo/BaseDemo/BaseDemo.unity`（或把 `ImageDownloaderDemo` 挂到任意 GameObject） | 两个独立 owner 画廊共享同一图片池，索引区间**刻意重叠**：共享 URL 只下载一次；单独销毁 A 或 B 只释放该画廊的引用，另一画廊仍在显示的纹理不会被淘汰；HUD 实时显示缓存数、内存、并发与在途请求 |
| 请求去重 | `Assets/Demo/RequestDedupDemo/RequestDedupDemo.unity` | 同一帧对同一 URL 发起 N 个请求，界面直接读取 runtime 计数器：`requests / downloads started / merged / cache hits`。可切换四种等价 URL 写法，同时验证归一化 |
| 并发 / FIFO | `Assets/Demo/ConcurrencyQueueDemo/ConcurrencyQueueDemo.unity` | 同一帧发起 N 个互不相同的 URL：前 `maxConcurrentDownloads` 个立即开始，其余进入等待队列；界面显示队首快照、槽位占用、并发/排队峰值与请求时间线，并用 `DownloadStarted` 重建实际启动顺序 |
| 失败分支 | `Assets/Demo/FailureHandlingDemo/FailureHandlingDemo.unity` | 内置仅监听 `127.0.0.1` 的 mock 服务器，复现：成功基线、HTTP 500、500×2 后恢复、`200 + image/png` 但响应体非图片、超时、HTTP 404。服务器日志逐次记录尝试，可离线验证 `1 + maxRetry`、重试恢复与统一 `null` 回调 |
| 引用计数 | `Assets/Demo/OwnerRefCountDemo/OwnerRefCountDemo.unity` | A/B/C 三个 owner 请求同一 URL，观察全局引用计数累加；销毁其中一个只释放它自己的那一份；在缓存上限压力下，被引用的纹理不会被淘汰 |
| 生命周期绑定 | `Assets/Demo/OwnerLifetimeDemo/OwnerLifetimeDemo.unity` | 同一帧对同一 URL 发起 3 个请求，其中两个发起者当帧销毁：绑定 owner 的请求回调被丢弃，未绑定的请求回调仍送达；三者共享同一次下载，排除“下载被取消”的干扰 |

> 除失败分支 Demo（完全走本机回环，可离线运行）外，其余场景都从 `picsum.photos` 取图，需要联网。

## 目录结构

```
Assets/
  Runtime/                              # 可复用 runtime 程序集
    ImageDownloader.cs                  # 加载器：调度、下载、缓存、引用计数
    MonoSingleton.cs                    # 极简自包含单例基类
    ImageDownloaderDemo.Runtime.asmdef  # 程序集 ImageDownloaderDemo.Runtime | references: []
  Demo/                                 # 示例程序集（单向引用 Runtime）
    ImageDownloaderDemo.Demo.asmdef     # 程序集 ImageDownloaderDemo.Demo | references: [ImageDownloaderDemo.Runtime]
    BaseDemo/                           # 综合 Demo：工具栏 + 实时 HUD + 两个 owner 画廊
      ImageDownloaderDemo.cs
      ImageDownloaderOwnerPanel.cs      # 独立 owner，负责加载共享池中的一个索引区间
      BaseDemo.unity
    RequestDedupDemo/                   # 请求去重 + URL 归一化
      RequestDedupDemo.cs
      RequestDedupDemo.unity
    ConcurrencyQueueDemo/               # 并发上限 + FIFO 等待队列
      ConcurrencyQueueDemo.cs
      ConcurrencyQueueDemo.unity
    FailureHandlingDemo/                # 超时 / 重试 / 解码失败 / HTTP 失败
      FailureHandlingDemo.cs
      LocalMockImageServer.cs
      FailureHandlingDemo.unity
    OwnerRefCountDemo/                  # 跨 owner 引用计数
      OwnerRefCountDemo.cs
      RefCountOwner.cs
      OwnerRefCountDemo.unity
    OwnerLifetimeDemo/                  # owner 销毁时的回调丢弃与引用释放
      OwnerLifetimeDemo.cs
      LifetimeDemoActor.cs
      OwnerLifetimeDemo.unity
```

## 设计说明

- **单一入口。** 所有请求都经过 `Request`，按固定顺序处理：绑定 owner → 缓存命中（同步回调）→ force-refresh 淘汰 → 合并已有请求 / 创建新下载。新增能力只需扩展这一条路径。
- **缓存命中是同步的。** 这让调用方写法最简单，但意味着回调可能在 `GetSprite` 返回前执行；不要假设它一定发生在后续帧。这是一处明确的 API 取舍。
- **内存估算为启发式。** 按真实纹理格式计算，并为 mip 链增加约 1/3；用于预算控制而非精确统计。
- **超大纹理保护。** 插入后淘汰时保护刚写入的 URL，避免单张纹理超过总预算时被立刻销毁、把已返回的句柄变成悬空引用。
- **被引用条目优先保留。** 如果存活 owner 占用的内存超过预算，淘汰会停止、预算被临时超出，直到引用释放。这是有意设计，避免销毁正在显示的纹理。
- **可观测性不侵入主流程。** `DownloadStarted` 在启动协程前触发，并对订阅者异常做隔离；统计计数器只做自增，供 Demo 读取，不影响调度逻辑。
- **owner tracker 对业务透明。** 使用 `HideFlags.HideAndDontSave` 挂在 owner 上，业务代码无需继承基类或手动清理。

## 工程化

- **编译期约束依赖边界。** runtime 是独立 asmdef 且 `references: []`，Demo 程序集单向引用它；“可独立复用”由编译器保证，而不是靠约定。
- **零配置演示。** 所有 Demo 使用 IMGUI 自绘界面，不依赖预制体或 UI 包，场景里只需要一个挂载组件的 GameObject。
- **失败可复现。** mock 服务器把“慢响应 / 5xx / 4xx / 非图片响应体”变成可重复的本地实验，而不是靠运气复现线上问题。
- **结论可验证。** 去重、并发、引用计数、生命周期四类行为都提供运行时计数器或事件记录，界面直接展示证据，而不是只写“已支持”。

## 运行环境

- Unity 2022.3 LTS（项目验证版本 `2022.3.30f1`）。
- 仅依赖 Unity 内置的 `UnityWebRequest` / `UnityWebRequestTexture`，无第三方包。
- 不绑定渲染管线；综合 Demo 使用 IMGUI，可直接跑在默认 3D 或 URP 工程中。

## 边界与后续

- 暂无磁盘缓存，进程重启后需要重新下载。
- 内存占用为估算值，不替代 Profiler。
- 缓存命中同步回调，接入响应式 UI 框架时需要留意调用时机。
- 暂无自动化测试；下一步计划把缓存 / 队列 / 引用计数抽成纯 C# 核心，并补充 EditMode 单元测试。
