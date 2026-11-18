# 整合包前置识别与 Rust 启动复查

## 诊断与修复

用户提供的诊断中，Lithium 要求 MixinExtras >=0.5.5，但启动器只识别了 mods 中的 0.5.4。核对官方 Fabric 0.20.1 的实际 JAR，其 fabric.mod.json 声明内嵌 mixinextras-fabric-0.5.5.jar。检查现已读取当前实例版本继承链中的实际加载器运行库，按已安装版本选择内嵌前置；不硬编码“某加载器一定提供某版本”。启动前、实例诊断和整合包更新均使用相同来源。真实缺失和版本冲突仍保留拦截。

版本比较补齐 Fabric 使用的预发布形式，包括 26.3-、beta、rc 和构建后缀，减少诊断中无法比较的条目。无法识别的语法仍返回未知，不猜测结果。

Rust 截图明确报告 languages missing。此前元数据缓存仅按索引 ID 保存；不同内容的索引都可能使用 ID 34，缓存会跨版本复用。现在资源索引缓存按其 SHA-1 区分，无哈希时按 URL 区分，不再读取仅有 ID 的旧索引缓存。另对旧 Nightly 已完成导出但缺语言资源的特定结果，调用官方 languages 子命令补齐后重新检查；其他准备失败不被吞掉。

本地扫描检查受管 builds 目录及已登记的导入目录，发现已有完整 Nightly 后更新列表，避免只依赖下载时保存的登记记录。其他位置继续使用原有本地导入选择。

缓存清理有确认步骤，删除可重新生成的 prepared、minecraft、temp 和受管构建的 .local；客户端、实例和存档保留，外部导入构建目录不被清理。运行 Rust 实例时拒绝清理。下一次需重新准备资源。

Rust 启动状态区分进程创建和可见窗口，Windows 尝试显示原生游戏窗口，退出后报告实际退出码。产品仍保留微软正版身份和拥有权检查、标准输入会话交接及独立实例存档。

## 验收范围

使用官方新下载的 D 盘隔离资源，不复制用户游戏、账号和存档。覆盖 Fabulously Optimized 26.3、官方签名 Horizon Nightly 的真实资源准备、旧索引缓存和缺失语言输入/输出的恢复，以及真实 Horizon 窗口和正常退出。

原生 Rust 窗口验收使用单独测试宿主的合成会话，仅验证正式进程适配器、窗口和退出链路；产品登录检查未关闭。未使用用户微软凭据，不宣称完成微软在线认证、联网服务器或世界游玩验收。

参考：[Fabric 官方加载器源码](https://github.com/FabricMC/fabric-loader)、[Fabric 0.20.1 官方分发](https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.20.1/)、[Horizon Launch API](https://github.com/horizon-rust/horizon/blob/main/docs/api/launch.md)、[Horizon 资源准备器](https://github.com/horizon-rust/horizon/blob/main/crates/horizon-prepare/src/main.rs)。

现阶段优先 Windows；保留平台无关核心和其他平台入口，不宣称 Linux/macOS 获得本批实测。

## 发布包验证

版本 1.0.0-preview.27；构建提交 41304e02a6b42576edcffb92bbef9a0cad1a9a8f。Windows 单文件 25755388 字节，SHA-256：9abfe678c55edb90528cb22b7d608fb58b2be1e357365ed324d7a31617c1b5df。1902 项检查通过（Core 1049、Horizon 协议 36、UI 810、真实整合包 7），另通过架构护栏、单文件启动退出、Horizon 实际转换、旧缓存/缺语言恢复、真实原生窗口和生产更新客户端本地验签。

## 已发布构建与远端验收

1.0.0-preview.27 已发布：[GitHub Release](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.27)。

- 构建源码：41304e02a6b42576edcffb92bbef9a0cad1a9a8f，标签指向该提交；后续文档提交不改变 EXE。
- Windows 单文件：25755388 字节，SHA-256：9abfe678c55edb90528cb22b7d608fb58b2be1e357365ed324d7a31617c1b5df。
- 1902 项检查通过，标准检查宿主使用实际发布的 Launcher 与密码库程序集；另通过架构护栏、完整裁剪框架的真实单文件启动退出、Fabulously Optimized 可见窗口及退出、官方 Horizon 转换、旧缓存/缺语言恢复、真实原生窗口及正常退出。
- 22 个草稿附件逐一核对 GitHub SHA-256 后公开；生产更新客户端重新从公开标签下载清单和程序，受信签名、版本判断、大小与文件哈希均通过。
- 发布渠道为 Pre-release；无正式版时 releases/latest 返回 404，不占用正式版入口。其他平台沿用先前受信签名及原始版本链接。
- 本批真实测试资料和临时夹具清理 3145360374 字节，保留公开验收 JSON、日志、截图及发布文件；未清理用户游戏目录或既有项目。

验收范围仍限本机 Windows 11。Rust 真实原生客户端窗口使用合成测试会话，未读取用户微软凭据，产品正版检查不变；微软在线认证、联网服务器、世界游玩及其他物理环境不在已通过范围。
