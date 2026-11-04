# preview.4：实例图标与整合包标志

显示顺序：用户手动封面 → 整合包自带图标（缺失时使用下载平台的项目图标）→ 对应加载器图标 → Minecraft 原版草方块。

- 游戏库卡片和列表不再重复使用首页的溪流插画；首页默认插画保留。
- 支持原版、Forge、Fabric、NeoForge、Quilt；自定义版本 ID 会结合版本 JSON 的依赖库、入口类和实例元数据识别加载器。
- Modrinth／CurseForge 导入读取根目录及 overrides 中的包级图标，支持套层压缩包及 CurseForge 自定义 overrides 目录；MultiMC／Prism 读取本地 iconKey 图标。不会使用模组或资源包子目录的图标。
- 在线安装缺少包内图标时，从对应平台获取项目标志。图标存入实例目录的 `.fangyu-pack-icon`，重启后无需网络。图标与导入使用同一暂存／回滚流程；取消安装不会留下新实例的半成品图标。
- 兼容已有实例的 `icon.png`、`.minecraft/pack.png`、`.minecraft/icon.png`。已安装的旧包若没有这些文件，不会在启动时额外请求网络补图。
- 手动封面使用裁切填充；包图标完整等比显示。移除手动封面后恢复包图标。图标缺失、损坏或平台请求失败时回退加载器标志，不影响游戏列表和安装。
- 图标读取上限 2 MiB；保留原始颜色和透明通道。此变更不增加第三方运行库。

## 原始素材来源

`Assets/Loaders` 使用原始图像与标志，未用生成图片重绘：

| 文件 | 来源 |
| --- | --- |
| vanilla.png | Mojang 客户端资产 `icons/icon_256x256.png`；SHA-1 `8030dd9dc315c0381d52c4782ea36c6baf6e8135` |
| fabric.png | https://fabricmc.net/assets/logo.png |
| forge.png | https://github.com/MinecraftForge/Installer/blob/2.0/src/main/resources/big_logo.png |
| forge-dark.png | https://github.com/MinecraftForge/Installer/blob/2.0/src/main/resources/big_logo_dark_mode.png |
| neoforge.png | https://github.com/NeoForged/NeoForge/blob/26.3.x/docs/assets/installer_profile_icon.png |
| quilt.svg | https://quiltmc.org/assets/img/logo.svg |

Forge 在绘制时仅显示原始字标左侧铁砧，随明暗主题选择官网的对应配色。Quilt 直接绘制 SVG 的原始路径。OptiFine 使用文字标识作为回退。

## 验证入口

- Core.Checks `--pack-icons`：包内与平台图标、来源识别、体积限制、请求失败、持久化与取消回滚。
- Desktop.Checks `--instance-icons`：加载器识别、真实卡片／列表渲染、明暗主题、封面优先级与重启恢复。
- Desktop.Checks `--workspace`：现有游戏库封面、导航和布局回归。

2026-11-04 验证：图标核心 15 项、整合包升级回归 34 项、图标界面 16 项、工作区回归 31 项，共 96 项通过。已查看卡片及明暗主题列表截图。

发布位置：[1.0.0-preview.4](https://github.com/Insomnia-7/mc-launcher/releases/tag/desktop-v1.0.0-preview.4)。按用户最新要求，后续改动完成后同步源码、单文件 EXE 与签名更新清单；见 [发布约定](release-policy.md)。
