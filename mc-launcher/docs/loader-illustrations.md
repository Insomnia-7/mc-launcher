# 安装方式的高清识别图

2026-11-16：安装方式不再使用 32 DIP 容器里的微型 Tile.Loader 图案，改用原生矢量绘制的 56 DIP 示意图，放入 72 DIP 底板，适配高倍渲染。标题、选择命令和安装逻辑保留。

| 加载器 | 识别特征 | 参考来源 |
| --- | --- | --- |
| Fabric | 米色叠层布料与缝线 | [Fabric 官网](https://fabricmc.net/) |
| Quilt | 紫、粉、蓝色拼布块 | [Quilt 官网](https://quiltmc.org/) |
| Forge | 蓝灰铁砧与金色细节 | [Forge 官网](https://files.minecraftforge.net/) |
| NeoForge | 橙色狐狸、深色耳尖和浅色面颊 | [NeoForged 官网](https://neoforged.net/) |
| 原版 | 立体草地方块 | 自绘安装方式示意图 |

实现位于 `Controls/LoaderIllustration.cs`，所有几何形状为矢量路径，不放大小位图，不增加图片解码和外部网络依赖。这些是识别用途的重绘示意图，不表示官方认证或背书；各项目名称与标识权利仍归各权利人。

Fabric 官网对未另行声明的内容标注 [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/)；参考其布料标识的这项重绘同样保留来源署名，并按该许可提供（不把它重新声明为 MIT）。软件其余独立代码的许可证不因此改变。其他标识也不因重绘而转移商标或原标识的权利。
