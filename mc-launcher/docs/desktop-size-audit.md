# Windows 单文件体积审查

2026-11-16。目标：在保留现有动画、功能、图像质量与自包含单文件交付的前提下，排除实际冗余；不按源码行数判断二进制收益。

## 基线

已发布 1.0.0-preview.21：31,739,103 字节（31.74 MB / 30.27 MiB），SHA-256 为 74155b242686a532383cf892313de7dd991bd0e6c533a98ec9c84f2e067fc704。

直接解析 .NET 6.0 格式的 Bundle 清单，以每个文件的实际压缩占用计数，而非把 NuGet 包目录或未裁剪的 bin 目录相加：

| 组成 | 基线字节 |
| --- | ---: |
| 自包含宿主、清单与对齐等开销 | 10,121,903 |
| 原生图形与文字渲染库 | 7,686,568 |
| 启动器程序集与内嵌资源 | 6,355,424 |
| .NET 和其他托管依赖 | 3,085,585 |
| BouncyCastle 密码库 | 2,613,440 |
| Avalonia 托管组件 | 1,876,183 |

当前构建已使用 partial 裁剪、去除符号和 .NET 单文件压缩，重复开启这些选项没有额外收益。自包含宿主本身包含运行时，不能把全部 30MB 都归因于功能代码。

## 采用的修改

1. 旧的 Assets/Brand/world-meadow-v2.png 为 1,783,450 字节，运行时代码与 XAML 已没有引用。仅从嵌入资源列表排除，保留源码素材；当前首页使用的 home-survival-banner-v1.png 和其他图片文件完全不变。排除旧图也让桌面程序集达到 SDK 的压缩收益阈值，因此最终收益大于该图片自身大小。
2. HorizonClient 仅静态调用 BouncyCastle 的 Ed25519.Verify。原先 partial 配置仍保留整个 5,155,728 字节的程序集；单独允许该依赖按引用裁剪后，实际程序集为 44,544 字节，Bundle 中占用 19,595 字节。保留已有签名校验实现和可信公钥，不自行重写密码算法。
3. 仅对 Avalonia.DesignerSupport 追加按引用裁剪；保留实际被运行时调用的代码。原生 Skia、HarfBuzz、ANGLE 与业务程序集的保留策略不变。
4. 审查下载路径发现 Parallel.ForEachAsync 和 SemaphoreSlim 使用相同上限，对同一批任务重复限流。移除冗余等待/释放，保留并发参数校验；现有并发、取消、重试、断点和完整性检查作为回归依据。

## 保留的实现与边界

- 启动、页面过渡、窗口与侧栏动画的代码和时间参数均未改动。
- 不更换或缩小当前图片，不降低图片质量，不移除中文、文本塑形、无障碍、渲染后备路径或现有用户功能。
- JSON/DI 使用反射的业务模型仍完整保留，不对整个应用强行 full trimming。Native AOT 涉及反射路径、原生库链接和交付方式变化，本批未将其作为等价替换。
- 不通过要求用户另装 .NET、首次联网下载运行库或外部壳来转移体积。保留自包含 EXE。
- 长文件、注释、局部变量名称本身不等于发布体积；没有为了减少行数改写已稳定的状态机和业务流程。

## 验证与最终产物

最终文件大小、源码提交、哈希、实际裁剪程序集回归、原生动画与字体检查、下载并发和 Horizon 签名校验结果记录在发布附件 verification.json、size-comparison.json 及 release-history.json。公开发布前须通过实际单文件启动和受信更新清单验证。

本机 Windows 11 验收；其他真实系统、DPI 和 Rust 实际进入世界仍待对应环境，不能以离线检查替代。

## 官方依据

- [.NET 单文件与压缩](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [.NET 裁剪选项](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trimming-options)
- [.NET SDK Bundle 压缩阈值实现](https://github.com/dotnet/runtime/blob/v10.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle/Bundler.cs)
- [Parallel.ForEachAsync 的并行选项](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.parallel.foreachasync?view=net-10.0)
