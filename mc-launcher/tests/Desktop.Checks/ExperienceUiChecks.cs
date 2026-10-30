using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Application.Accounts;
using Launcher.Application.Auth;
using Launcher.Application.Install;
using Launcher.Application.Mods;
using Launcher.Application.Operations;
using Launcher.Desktop.Controls;
using Launcher.Domain.Content;

internal sealed class AppearanceFixture:IPlayerAppearanceClient,ISkinService
{
    public int Uploads,CapeChanges,Resets;
    public bool RejectUpload;
    public readonly byte[] Skin=MakeSkin();
    public Task<AppearanceProfile> GetAsync(string token,CancellationToken ct=default)=>Task.FromResult(new AppearanceProfile("00112233445566778899aabbccddeeff","FixturePlayer","https://textures.minecraft.net/texture/fixture","classic",[new("owned","测试账号已拥有披风","https://textures.minecraft.net/texture/cape",true)]));
    public Task UploadSkinAsync(string token,byte[] png,string model,CancellationToken ct=default){if(RejectUpload)throw new HttpRequestException("登录已过期，请重新验证账号。");Uploads++;return Task.CompletedTask;}
    public Task ResetSkinAsync(string token,CancellationToken ct=default){Resets++;return Task.CompletedTask;}
    public Task SetCapeAsync(string token,string? capeId,CancellationToken ct=default){CapeChanges++;return Task.CompletedTask;}
    public Task<byte[]> GetTextureAsync(string url,CancellationToken ct=default)=>Task.FromResult(Skin);
    public Task<SkinFetchResult> FetchAsync(string uuid,string name,CancellationToken ct=default)=>Task.FromResult(SkinFetchResult.Failure("fixture"));
    public static byte[] MakeSkin()
    {
        using var bitmap=new WriteableBitmap(new PixelSize(64,64),new Vector(96,96),PixelFormat.Rgba8888,AlphaFormat.Unpremul);
        using(var frame=bitmap.Lock())
        {
            var bytes=new byte[frame.RowBytes*64];
            for(var y=0;y<64;y++)for(var x=0;x<64;x++){var p=y*frame.RowBytes+x*4;bytes[p]=(byte)(80+x*2);bytes[p+1]=(byte)(100+y);bytes[p+2]=90;bytes[p+3]=255;}
            Marshal.Copy(bytes,0,frame.Address,bytes.Length);
        }
        using var stream=new MemoryStream();bitmap.Save(stream);return stream.ToArray();
    }
}
internal static class ExperienceUiChecks
{
    public static void Run(MainViewModel model,Window window,AppearanceFixture fixture,ResourceFixture resources,Action<int> pump,Action<string,bool> check,Action<string,bool> capture,string root)
    {
        void Wait(Task task){var clock=System.Diagnostics.Stopwatch.StartNew();while(!task.IsCompleted&&clock.ElapsedMilliseconds<8000)pump(10);if(!task.IsCompleted)throw new TimeoutException("体验检查未完成。");task.GetAwaiter().GetResult();pump(80);}
        void Until(Func<bool> ready){for(var i=0;i<800&&!ready();i++)pump(10);if(!ready())throw new TimeoutException("体验状态未就绪。");pump(80);}
        model.IsInstanceSection=true;model.SelectedInstance=model.Instances.First(i=>i.VersionId=="1.21.4");
        var game=Path.Combine(model.GameRoot,"instances",model.SelectedInstance.InstanceId,".minecraft");Directory.CreateDirectory(Path.Combine(game,"config"));Directory.CreateDirectory(Path.Combine(game,"saves","test"));
        File.WriteAllText(Path.Combine(game,"config","test.txt"),"before");File.WriteAllText(Path.Combine(game,"saves","test","level.dat"),"current-save");
        Wait(model.OpenWardrobeCommand.ExecuteAsync(null));
        check("角色衣柜通过独立入口打开且保留账号区别",model.IsWardrobeTab&&model.IsExperienceOpen&&model.LocalSkinHint.Contains("离线"));
        var skinPath=Path.Combine(root,"test-skin.png");File.WriteAllBytes(skinPath,fixture.Skin);model.LookName="旅人";Wait(model.ImportWardrobeSkinFileAsync(skinPath));
        check("本地导入只加入衣柜，未上传官方账号",model.WardrobeLooks.Count==1&&model.PreviewSkin?.Length>0&&fixture.Uploads==0);
        var preview=window.GetVisualDescendants().OfType<PlayerPreview>().Single();var before=preview.Rotation;preview.Focus();
        window.KeyPress(Key.Right,Avalonia.Input.RawInputModifiers.None,PhysicalKey.ArrowRight,null);window.KeyRelease(Key.Right,Avalonia.Input.RawInputModifiers.None,PhysicalKey.ArrowRight,null);pump(50);
        check("角色模型可使用键盘旋转",preview.Rotation>before);
        capture("wardrobe-offline",true);
        window.Width=900;window.Height=640;pump(80);var close=window.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="CloseExperienceButton");
        check("最小窗口玩家中心关闭入口仍完整可见",close.TranslatePoint(new Point(),window) is {} p&&p.X>window.ClientSize.Width/2&&p.Y>=0&&p.X+close.Bounds.Width<=window.ClientSize.Width);
        var apply=window.GetVisualDescendants().OfType<Button>().Single(b=>ReferenceEquals(b.Command,model.UseLocalLookCommand));
        var label=apply.GetVisualDescendants().OfType<TextBlock>().Single(t=>t.Text=="应用本地外观");
        check("衣柜主操作文字沿用按钮前景色",Equals(label.Foreground,apply.Foreground));
        capture("wardrobe-compact",true);window.Width=1180;window.Height=800;pump(50);
        model.Account=new("00112233445566778899aabbccddeeff","FixturePlayer","fixture-token","fixture-refresh",DateTimeOffset.UtcNow.AddHours(12));
        Wait(model.RefreshWardrobeCommand.ExecuteAsync(null));Wait(model.ImportWardrobeSkinFileAsync(skinPath));Wait(model.UploadWardrobeSkinCommand.ExecuteAsync(null));
        check("正版衣柜执行皮肤上传并读取账号已拥有披风",fixture.Uploads==1&&model.OwnedCapes.Single().Id=="owned");
        Wait(model.ApplyOwnedCapeCommand.ExecuteAsync(null));Wait(model.HideOfficialCapeCommand.ExecuteAsync(null));check("正版披风支持切换与隐藏",fixture.CapeChanges==2);
        fixture.RejectUpload=true;Wait(model.UploadWardrobeSkinCommand.ExecuteAsync(null));check("接口失败不会显示上传成功",model.WardrobeHint.Contains("登录已过期")&&fixture.Uploads==1);fixture.RejectUpload=false;
        capture("wardrobe-online",true);model.Account=null;model.CloseExperienceCommand.Execute(null);pump(50);
        model.OpenInstanceHistoryCommand.Execute(null);model.SnapshotName="界面快照";model.IncludeWorlds=false;Wait(model.CreateSnapshotCommand.ExecuteAsync(null));
        check("界面可创建并列出实例快照",model.Snapshots.Any(s=>s.Name=="界面快照"));
        model.SelectedSnapshot=model.Snapshots.First(s=>s.Name=="界面快照");File.WriteAllText(Path.Combine(game,"config","test.txt"),"after");model.RestoreScope=0;
        model.RestoreSnapshotCommand.Execute(null);check("恢复快照需要确认且说明保留存档",model.IsConfirmOpen&&model.ConfirmMessage.Contains("保留当前存档"));model.ConfirmCancelCommand.Execute(null);
        check("取消快照恢复不改变当前配置",File.ReadAllText(Path.Combine(game,"config","test.txt"))=="after");
        model.RestoreSnapshotCommand.Execute(null);model.ConfirmOkCommand.Execute(null);Until(()=>!model.ExperienceBusy);
        check("界面恢复配置并保留当前存档",File.ReadAllText(Path.Combine(game,"config","test.txt"))=="before"&&File.ReadAllText(Path.Combine(game,"saves","test","level.dat"))=="current-save");
        model.MemoryMb=6144;model.SaveSettingsCommand.Execute(null);model.PresetName="建筑设置";Wait(model.SavePresetCommand.ExecuteAsync(null));
        model.MemoryMb=2048;model.SaveSettingsCommand.Execute(null);model.SelectedPreset=model.Presets.Single(p=>p.Name=="建筑设置");Wait(model.ApplyPresetCommand.ExecuteAsync(null));check("设置预设通过界面实际保存并应用",model.MemoryMb==6144);
        var candidate=new ModUpdateCandidate("test","test.jar","1","2",true,null,0,"modrinth"){VersionId="v2"};var row=new MainViewModel.ModUpdateRow("test","test.jar","测试模组","1 → 2","Modrinth",candidate);
        model.ModUpdates.Add(row);model.LockModUpdateCommand.Execute(row);check("界面支持模组版本锁定",model.LockedModFiles.Contains("test.jar")&&model.IsModUpdateAllowed?.Invoke(candidate)==false);
        model.UnlockModUpdateCommand.Execute("test.jar");model.UpdateChannel="测试版";check("更新频道明确区分测试版与正式版",model.ModUpdateReleaseChannel=="beta");model.UpdateChannel="正式版";
        capture("instance-history",true);
        model.CloseExperienceCommand.Execute(null);model.IsInstanceSection=false;var vm=model.Download;vm.SelectedCategory=vm.Categories.First(c=>c.Category==DownloadCategory.MinecraftVersion);vm.SelectVersionCommand.Execute(new VersionItem("1.21.4","release",DateTimeOffset.UtcNow));
        resources.PendingDownload=new(TaskCreationOptions.RunContinuationsAsynchronously);var running=vm.InstallVersionCommand.ExecuteAsync(null);Until(()=>model.DurableTasks.Any(t=>t.CanPause));
        model.OpenTaskCenterCommand.Execute(null);var task=model.DurableTasks.Single(t=>t.CanPause);model.PauseDurableTaskCommand.Execute(task);Wait(running);
        check("任务中心可暂停安装并恢复操作入口",task.CanResume&&!model.IsBusy&&!vm.IsBusy&&task.StateText.Contains("暂停"));
        resources.PendingDownload=null;Wait(model.ResumeDurableTaskCommand.ExecuteAsync(task));check("任务中心恢复原任务并完成安装",task.Record.State==DurableTaskState.Completed);
        Wait(model.InspectInstanceCommand.ExecuteAsync(null));check("中文体检展示依据和可操作建议",model.InspectionIssues.Count>0&&model.InspectionIssues.All(i=>i.Evidence.Length>0&&i.Advice.Length>0));
        capture("task-center",true);model.CloseExperienceCommand.Execute(null);model.IsInstanceSection=true;pump(80);
        check("关闭玩家中心后释放预览纹理与弹窗命中",!model.HasOverlay&&model.PreviewSkin is null&&model.PreviewCape is null);
    }
}
