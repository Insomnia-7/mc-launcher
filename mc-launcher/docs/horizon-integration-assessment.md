# Horizon 整合可行性与授权分析

检索日期：2026-11-16（北京时间）。对象：[horizon-rust/horizon](https://github.com/horizon-rust/horizon)。以下区分上游公开事实、方隅的工程建议和仍未查明的权利问题；不把开源许可证等同于 Minecraft 权利人的授权。

## 结论

**技术上可以整合，建议作为独立的实验性游戏后端；不应替换现有 Java 启动链。MIT 允许使用 Horizon 代码，但不能据此承诺整个游戏重写与发行方式没有侵权风险。免费公益也不是自动豁免。**

本批仅做调研，没有把 Horizon 引擎、下载器、认证流程或游戏文件加入方隅，也没有运行上游程序。

## 它究竟是什么

Horizon 重写的是 Minecraft Java Edition 26.3 的游戏客户端与逻辑，不只是一个 Rust 编写的启动器。其 README 表示 Java 模组不能作为 Java 代码在其中运行；导入整合包中的数据或资产不等于执行 Fabric / Forge / NeoForge 模组。[项目说明](https://github.com/horizon-rust/horizon/blob/main/README.md)

上游把“与原版行为一致”作为目标，并明确表示尚未处处达到。因此不能从 Rust 语言或可运行状态推导出全面兼容、必然更快，或所有服务器与存档都安全兼容。[重写状态](https://github.com/horizon-rust/horizon/blob/main/docs/rust-parity.md)

## C# / Avalonia 能否接入

可以。Avalonia 继续负责界面，C# 调用独立的 Rust 可执行文件即可，无须把界面改成 Rust，也无须先编写 Rust 与 C# 的内存互操作层。

上游专门提供给第三方启动器的 Launch API，目前文档标记为 **experimental / 0.1.0**，可查询客户端与资源准备器的能力、传入实例路径并接收退出结果。账号会话支持标准输入或一次性文件；令牌不能放在命令行或日志中。[Launch API](https://github.com/horizon-rust/horizon/blob/main/docs/api/launch.md)

| 方式 | 方隅中的效果 | 判断 |
| --- | --- | --- |
| 调用 Horizon 自带启动器的 CLI | 复用上游安装和登录流程，但会出现第二套启动器状态和账号管理 | 适合早期验证 |
| C# 调用 horizon-prepare / horizon-client | 保留方隅 UI，单独管理 Horizon 构建、资源和实例 | 推荐的整合方向 |
| 把整个 Rust 引擎作为库嵌入进程 | 涉及接口、渲染、线程、生命周期与崩溃边界 | 当前没有必要 |

其中 CLI 的安装、准备和启动命令已有上游说明；表中的成本和选择是本次工程判断。[启动器命令](https://github.com/horizon-rust/horizon/blob/main/docs/launcher-commands.md)

## 推荐的接入边界

1. 在实例层区分 Java 与 Horizon，Horizon 不作为 Fabric 等加载器的同级选项。既有 Java 整合包仍走原链路。
2. Horizon 使用独立的构建目录和实例目录。现有世界先复制到实验实例，不直接改写用户原存档。
3. 从所选构建查询支持的 Minecraft / Java 版本和参数，校验用户合法游戏文件，调用资源准备器。不要把“Rust 游戏”理解为首次准备也完全不需要 Java。
4. 在方隅验证玩家身份与游戏所有权后，通过受支持的会话接口启动。资源准备、进程退出和诊断结果接入现有任务与日志页面。
5. 锁定实际构建与能力，先完成安装、取消、重试和回退验证，再开放给用户主动选择。

上游第三方启动器指南描述了构建布局、资源准备和独立实例；其 API 稳定性文档明确允许实验接口在后续版本变动。以上目录隔离与分步开放是对方隅的建议。[接入指南](https://github.com/horizon-rust/horizon/blob/main/docs/api/launch-guide.md)、[稳定性约定](https://github.com/horizon-rust/horizon/blob/main/docs/api/stability.md)

下载校验不能只看文件名或 GitHub 的 Latest：上游发布流程以 Nightly 为主，标签和资产会滚动。应记录实际构建身份并验证对应校验清单与签名。上游安全文档存在不同阶段的描述，实施时还需核对选中构建的真实资产和验证代码，不能只凭某一段文档认定全部发布物已验签。[发布流程](https://github.com/horizon-rust/horizon/blob/main/docs/release-flow.md)、[安全说明](https://github.com/horizon-rust/horizon/blob/main/docs/security.md)

上游完整产品还包含商店、外观和其他扩展入口。你的目标是公益单一 MC 启动器，因此更适合只接入客户端启动能力；上游也说明了关闭部分 extras 的构建方式，但非默认客户端构建不能假定具有相同验收覆盖。[派生构建范围](https://github.com/horizon-rust/horizon/blob/main/docs/forking.md)

## 风险要分开判断

### 1. Horizon 代码的 MIT 许可证

上游 LICENSE 允许使用、修改和分发代码，要求保留版权及许可声明，并不提供不侵权担保。与方隅现有 MIT 代码在许可证层面可以组合；具体引入的依赖、字体、图标和二进制仍需按各自许可证核查。不能把顶层 MIT 套用于所有第三方内容。[Horizon LICENSE](https://github.com/horizon-rust/horizon/blob/main/LICENSE)

### 2. Minecraft 游戏文件与重写本身

Minecraft EULA 区分原创 Mod 与含有其版权代码或内容的游戏/修改版本，并限制未经许可分发游戏内容。因此可以设计为由合法玩家在本地准备资源，不能把客户 JAR、纹理、音频或准备生成的游戏资源直接附进方隅发行包。是否属于受保护内容的复制或衍生作品，不能仅靠换一种编程语言判断。[Minecraft EULA](https://www.minecraft.net/en-us/eula)

还不能把该项目描述为已经验证的“净室重写”：其 port-ledger 文档明确说明会读取原版 JAR 和本地反编译源码来做方法映射。这是需要进一步审查开发过程与具体代码来源的事实，不足以单独断言侵权，也不足以排除风险。[Port ledger](https://github.com/horizon-rust/horizon/blob/main/docs/port-ledger.md)

### 3. 公益和免费

你的公益定位有助于避免收费、广告或收益相关问题，但不替代代码和内容授权。Minecraft Usage Guidelines 对品牌/资产的使用约定还明确把向他人免费分享纳入其 commercial use 的定义；这不等于认定你在盈利，而是其规则适用范围。该指南也要求避免官方背书印象和未经允许再分发游戏文件。[Minecraft Usage Guidelines](https://www.minecraft.net/en-us/usage-guidelines)

### 4. 登录应用身份

Horizon 明确要求派生启动器使用自己的 Azure 应用注册和相应 Minecraft API 审核，不能复制、下载或借用 Horizon 的 client ID。MIT 授权代码，不转让应用注册资格。登录接口审核通过也不等于 Minecraft 权利人批准整个 Rust 重写项目。[派生项目说明](https://github.com/horizon-rust/horizon/blob/main/docs/forking.md)

账号所有权检查、会话传递与失败处理应遵循相应接口；不能因为原生游戏程序能够被直接执行，就跳过启动器应承担的授权检查。[账号设计](https://github.com/horizon-rust/horizon/blob/main/docs/microsoft-account.md)

## 对方隅的建议

可以继续做隔离的接入原型，先验证 Launch API 与现有 C# 账号/任务抽象，再决定是否公开提供可选 Horizon 实例。公开发行前，核清所用代码和依赖许可、游戏资源不随包分发、独立登录应用身份以及品牌声明；对重写代码来源和拟采用的发行方式，建议取得熟悉软件版权的法律专业意见。

**当前可确认的是工程可行和 MIT 的代码许可；不能确认的是 Minecraft 权利人对整个重写及你的具体发行方式作出了授权。** 上述判断来自截至检索日的公开文档，没有完整代码来源审计、法律意见书或权利人针对方隅的书面许可。
