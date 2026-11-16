# 方隅 CurseForge 搜索接口申请说明

核对日期：2026-11-16。当前状态：构建接入能力已准备，用户提供的凭据被官方搜索端点拒绝，未放入公开发行包。获取可用于第三方资源搜索的 API Key 后再启用。

## 先分清两种接口

| 项目 | 作者上传接口 | 启动器资源搜索接口 |
| --- | --- | --- |
| 文档 | CurseForge Upload API | CurseForge REST API / 第三方应用申请 |
| 用途 | 上传和管理作者自己的文件；查询上传可选的版本标签 | 搜索公开模组、整合包等资源、读取项目和文件信息 |
| 认证 | X-Api-Token | x-api-key |
| 典型路径 | /api/projects/{id}/upload-file、/api/game/versions | api.curseforge.com/v1/mods/search?gameId=432 |

用户提供的 [API 页面](https://support.curseforge.com/support/solutions/articles/9000197321-curseforge-api) 标题为 **CurseForge Upload API**。其中 Game Versions 不是模组搜索。如果凭据由该页的作者 API Tokens 入口生成，它不能与第三方搜索 Key 互换。没有对用户项目执行上传、修改或删除操作。

本次对官方搜索端点分别通过当前网络及直连验证，均为 HTTP403，原文为 `Forbidden: API Key missing or invalid`。这能确认凭据未通过该接口认证，不能凭长度或外观断言其具体来源。分类端点可返回200也不证明搜索授权成功。

## 你需要做什么

1. 打开 [官方第三方 API 申请说明](https://support.curseforge.com/support/solutions/articles/9000208346)，通过其链接进入 [API Applications 表单](https://forms.monday.com/forms/dce5ccb7afda9a1c21dab1a1aa1d84eb?r=use1)。具体表单字段以页面为准，联系信息由项目维护者填写。
2. 项目填写“方隅 / Fangyu”，仓库填写 https://github.com/Insomnia-7/mc-launcher，说明是免费开源的 Minecraft 启动器，申请搜索、项目/文件读取及作者允许的下载能力。
3. 明确说明需要让公开桌面客户端直接使用同一项目 Key，请官方确认是否允许分发、适用配额和数据缓存规则。不要只把“能生成上传Token”当作获得这些权限。
4. 获批后把对应搜索 API Key 保存到本地文件，再提供路径。先测试搜索成功，随后才制作内置 Key 的发行版。不需要把实际 Key 贴到聊天或提交到 GitHub。

通用 [第三方 API 条款](https://support.curseforge.com/support/solutions/articles/9000207405-curseforge-3rd-party-api-terms-and-conditions) 限制向第三方披露 Key。用户此前确认拥有许可，但本次没有取得许可原文，不能将此写成已独立核验的桌面分发授权。若官方允许公开客户端内置，Key仍可从程序中提取，混淆或加密不能使分发给用户的凭据变成服务器端秘密。

## 可用于申请的项目说明

**Project name:** Fangyu (方隅)

**Project URL:** https://github.com/Insomnia-7/mc-launcher

**Description:** Fangyu is a free, open-source Minecraft Java Edition launcher built with C#, .NET and Avalonia. We would like to integrate CurseForge's public catalog so users can search for mods, modpacks, resource packs, data packs and shaders, view project metadata and compatible file versions, and download files only where third-party distribution is permitted by the authors. The requested integration does not need author upload or project-management privileges.

**Distribution and API access:** We intend to distribute a native desktop client and would like users to access the catalog without obtaining individual API keys. Please confirm the approved authentication and distribution model, whether an application-wide key may be included in publicly distributed desktop binaries, applicable quotas, and permitted metadata/icon caching. We understand that credentials included in desktop software are extractable. Expected request volume depends on active users; we can provide estimates as the project grows.

此说明没有提交到外部表单，也没有代填个人资料或接受服务协议。

## 费用需要确认

公开第三方API条款第2.3条没有给出统一单次搜索价格或固定免费额度，而是约定超过官方设定的配额后，可能要求另签包含付费条件的许可协议；未达成协议可能停止访问。不能把“公益免费”直接推定为无限免费API。

方隅当前方案由用户客户端直接访问CF，不部署方隅服务器中转搜索或下载，因此不新增服务器、域名与中转带宽费用。这是当前实现的工程成本判断，不是CF的收费承诺。申请时请明确询问免费资格、调用额度、超额处理，以及是否支持在不产生费用的前提下限流或停用。

可补充到英文申请：**This is a non-commercial, free and open-source project. Please confirm whether this use is free of charge, the applicable request quotas, what happens when a quota is exceeded, and whether access can be rate-limited or disabled instead of incurring any automatic charges.**

## 构建接入

Windows发布脚本支持 `-CurseForgeApiKeyFile <私有文件路径>`。文件作为可选程序集资源注入，不把真实Key写进仓库。现有环境变量和本机配置优先于项目默认Key。源代码公开构建没有Key也能编译，届时明确显示项目接口未启用。

发布前必须用真实官方搜索验证权限，并取得适用分发许可。本批凭据认证失败，公开程序不含该凭据。API实现参考 [官方REST文档](https://docs.curseforge.com/rest-api/)。
