# 草案：让主程序支持单文件发布（源码改动）

三处 `.csproj` + 一处 `MainViewModel`，另有一条错误文案建议。**本次没有改仓库源码**（任务边界只允许新增
`packaging/single-file/**`），下面是逐字可套用的补丁，已在隔离副本上实测通过（构建 + 首帧 + 端到端自更新）。

路径都相对仓库根。

---

## 1. `mc-launcher/src/Launcher.App/Launcher.App.csproj`

### 1.1 单文件时不再引用更新器工程

`-p:PublishSingleFile=true` 会把这个属性传给被引用项目，于是「非自包含可执行文件引用自包含可执行文件」→
`NETSDK1151`。加条件让单文件发布时这条引用消失：

```diff
   <ItemGroup>
     <ProjectReference Include="..\Launcher.Application\Launcher.Application.csproj" />
     <ProjectReference Include="..\Launcher.Infrastructure\Launcher.Infrastructure.csproj" />
-    <!-- 只为了让它先构建：更新器要整份复制到输出目录的 updater\ 下 -->
-    <ProjectReference Include="..\..\tools\Launcher.Updater\Launcher.Updater.csproj">
+    <!-- 只为了让它先构建：更新器要整份复制到输出目录的 updater\ 下。
+         单文件发布时不能再引用它：NETSDK1151（自包含可执行文件不能被非自包含可执行文件引用）。 -->
+    <ProjectReference Include="..\..\tools\Launcher.Updater\Launcher.Updater.csproj"
+                      Condition="'$(PublishSingleFile)' != 'true'">
       <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
       <Private>false</Private>
     </ProjectReference>
   </ItemGroup>
```

### 1.2 新增：把已发布的更新器编进主程序

```diff
+  <!-- 单文件发布：把已发布的 mclupd.exe 编进主程序，运行时解到 %TEMP% 再跑（见 MainViewModel.ExtractEmbeddedUpdater）。 -->
+  <ItemGroup Condition="'$(UpdaterPayload)' != '' And Exists('$(UpdaterPayload)')">
+    <EmbeddedResource Include="$(UpdaterPayload)" LogicalName="Launcher.Updater.mclupd.exe" />
+  </ItemGroup>
+
   <Target Name="CopyUpdater" AfterTargets="Build"
-          Condition="Exists('$(MSBuildThisFileDirectory)..\..\tools\Launcher.Updater\bin\$(Configuration)\net10.0\mclupd.exe')">
+          Condition="'$(PublishSingleFile)' != 'true' And Exists('$(MSBuildThisFileDirectory)..\..\tools\Launcher.Updater\bin\$(Configuration)\net10.0\mclupd.exe')">
```

`LogicalName` 是**固定的资源名**，`ExtractEmbeddedUpdater` 按它取流。`Condition` 里的 `Exists()`
保证多文件发布（不传 `-p:UpdaterPayload=`）时这条 `ItemGroup` 完全不存在，行为与现在一致。

---

## 2. `mc-launcher/src/Launcher.App/ViewModels/MainViewModel.cs`

`FindUpdater()` 在找不到 `updater\mclupd.exe` / 同级 `mclupd.exe` 时回落到内嵌资源：

```diff
-    /// <summary>更新器位置：安装目录下的 updater\mclupd.exe，其次和主程序同级。</summary>
+    /// <summary>更新器位置：安装目录下的 updater\mclupd.exe，其次和主程序同级；单文件发布时解出编进主程序的那一份。</summary>
     private static string? FindUpdater()
     {
         var baseDirectory = AppContext.BaseDirectory;
         foreach (var candidate in new[]
                  {
                      Path.Combine(baseDirectory, "updater", "mclupd.exe"),
                      Path.Combine(baseDirectory, "mclupd.exe"),
                  })
         {
             if (File.Exists(candidate))
                 return candidate;
         }
 
-        return null;
+        return ExtractEmbeddedUpdater();
     }
+
+    /// <summary>
+    /// 单文件发布没有 updater\ 目录：把编进主程序的 mclupd.exe 解到 %TEMP% 再跑。
+    /// 目录名带程序集版本与资源长度，升级后自然换一份，不会复用旧更新器。
+    /// </summary>
+    private static string? ExtractEmbeddedUpdater()
+    {
+        const string resourceName = "Launcher.Updater.mclupd.exe";
+        var assembly = typeof(MainViewModel).Assembly;
+        using var stream = assembly.GetManifestResourceStream(resourceName);
+        if (stream is null)
+            return null;
+
+        var version = assembly.GetName().Version?.ToString() ?? "0";
+        var directory = Path.Combine(Path.GetTempPath(), "mclauncher-updater", $"{version}-{stream.Length}");
+        var path = Path.Combine(directory, "mclupd.exe");
+        if (File.Exists(path) && new FileInfo(path).Length == stream.Length)
+            return path;
+
+        Directory.CreateDirectory(directory);
+        var temporary = path + ".tmp";
+        using (var file = File.Create(temporary))
+            stream.CopyTo(file);
+        File.Move(temporary, path, overwrite: true);
+        return path;
+    }
```

要点：

- **必须用 `typeof(MainViewModel).Assembly`** 而不是 `Assembly.GetExecutingAssembly()`：WPF 单文件下
  代码可能在 `Launcher.App.dll` 里，两者通常相同，但显式取 App 程序集更稳。
- 目录名带 `版本 + 资源长度`：更新器随主程序升级而变，长度不同 → 换目录；相同 → 直接复用，不重复写盘。
- 写 `.tmp` 再 `File.Move`：避免两个实例并发解压时读到半截文件。
- 返回 `null` 时沿用原有错误文案，不需要新分支。

---

## 3. 建议顺手改的错误文案（可选）

`ApplyUpdateAsync` 里这条在单文件下会误导用户（根本没有 `updater\` 目录）：

```diff
         var updater = FindUpdater();
         if (updater is null)
         {
-            StatusText = "找不到更新器 mclupd.exe（应该在安装目录的 updater\\ 下）";
+            StatusText = "找不到更新器 mclupd.exe（安装包可能不完整，请重新下载）";
             return;
         }
```

---

## 4. 验证清单（改完应该能过）

```powershell
# 1) 多文件发布行为不变（不传 UpdaterPayload）：17 个根文件，没有 updater\ 目录
#    （CI 的 updater\mclupd.exe 来自下面第 2 步那条独立的 -o out/updater 发布，不靠 CopyUpdater）
dotnet publish mc-launcher/src/Launcher.App -c Release -o out

# 2) 单文件发布（推荐形态）：应产出 1 个 2.39 MB 的 MCLauncher.exe
#    注意 payload 必须落在 out\ 之外，否则它会跟着进 update.json 的清单
dotnet publish mc-launcher/tools/Launcher.Updater -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o payload/updater
dotnet publish mc-launcher/src/Launcher.App -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none -p:DebugSymbols=false `
  -p:UpdaterPayload=payload/updater/mclupd.exe -o out

# 3) 回归：现有检查项
dotnet mc-launcher/tests/Core.Checks/bin/Release/net10.0/Core.Checks.dll
dotnet mc-launcher/tests/Architecture.Check/bin/Release/net10.0/Architecture.Check.dll
dotnet mc-launcher/tests/App.Checks/bin/Release/net10.0-windows/App.Checks.dll
```

注意第 2 步的 `-p:IncludeNativeLibrariesForSelfExtract=true` **不能省**：WPF 有原生依赖
（`wpfgfx_cor3.dll` 等），缺了它单文件进程起不来。
另外 `-p:DebugType=none -p:DebugSymbols=false` 也别省：单文件只合并程序集、不合并符号，
默认会留下 4 个 `.pdb`，「一个 exe」就成了假象。
