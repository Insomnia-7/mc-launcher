using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Launcher.App;
using Launcher.App.ViewModels;
using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Desktop;
using Launcher.Desktop.Controls;

internal static class SettingsSyncUiChecks
{
    public static void Run(MainViewModel model,Window window,Action<int> pump,Action<string,bool> check,Action<string,bool> capture,string root)
    {
        void Until(Func<bool> condition){var clock=System.Diagnostics.Stopwatch.StartNew();while(!condition()&&clock.ElapsedMilliseconds<15000)pump(10);if(!condition())throw new TimeoutException("设置同步状态超时");pump(100);}
        void Wait(Task task){Until(()=>task.IsCompleted);task.GetAwaiter().GetResult();}
        T Find<T>(string name) where T:Control=>window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
        const string source="sync-home",target="sync-survival";
        string FileFor(string id)=>Path.Combine(model.GameRoot,"instances",id,".minecraft/options.txt");
        foreach(var (id,name) in new[]{(source,"日常世界"),(target,"冒险整合包")})
        {
            var directory=Path.GetDirectoryName(FileFor(id))!;Directory.CreateDirectory(directory);
            new InstanceMetadata(name,null,null,null,"1.21.4",null,"1.21.4",false).Write(model.GameRoot,id);
        }
        File.WriteAllText(FileFor(source),"key_key.jump:key.keyboard.space\nsoundCategory_master:0.7\nrenderDistance:20\n");
        File.WriteAllText(FileFor(target),"key_key.jump:key.keyboard.w\nsoundCategory_master:0.2\nrenderDistance:8\n");
        model.TryUseGameRoot(model.GameRoot,out _);model.SelectedInstance=model.Instances.Single(i=>i.InstanceId==target);model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="glacier");model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");
        model.OpenSettingsSyncCommand.Execute(null);pump(100);
        var service=new SettingsSync(AppPaths.DataDirectory,model.GameRoot);
        check("设置提供独立游戏同步页且默认关闭",model.ShowsSettingsSync&&!model.IsNetworkSettings&&!model.IsAppearanceSettings&&service.Read() is null&&Find<Button>("SettingsSyncTab").IsEffectivelyVisible);
        model.SyncSource=model.SyncInstances.Single(i=>i.Id==source);model.SaveSettingsSyncSourceCommand.Execute(null);check("保存共享来源先说明同步范围",model.IsConfirmOpen);model.ConfirmCancelCommand.Execute(null);
        check("取消不建立共享方案",service.Read() is null);
        model.SaveSettingsSyncSourceCommand.Execute(null);model.ConfirmOkCommand.Execute(null);Until(()=>!model.SettingsSyncBusy&&service.Read() is not null);
        model.SyncTarget=model.SyncInstances.Single(i=>i.Id==target);model.FollowSettings=true;model.FollowKeys=true;model.FollowSound=true;model.FollowLanguage=false;Wait(model.SaveSettingsSyncTargetCommand.ExecuteAsync(null));
        Wait(model.PreviewSettingsSyncCommand.ExecuteAsync(null));
        check("同步预览列出本地和共享值且冲突默认保留",model.HasSettingsSyncPlan&&model.SyncChanges.Count==2&&model.SyncChanges.All(c=>!c.Apply)&&File.ReadAllText(FileFor(target)).Contains("0.2"));
        capture("settings-sync-preview-light",true);model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="aurora");pump(60);capture("settings-sync-preview-dark",true);model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="glacier");
        model.SyncChanges.Single(c=>c.Change.Key=="soundCategory_master").Apply=true;model.ApplySettingsSyncCommand.Execute(null);model.ConfirmCancelCommand.Execute(null);
        check("取消应用不修改游戏设置",File.ReadAllText(FileFor(target)).Contains("0.2"));
        model.ApplySettingsSyncCommand.Execute(null);model.ConfirmOkCommand.Execute(null);Until(()=>!model.SettingsSyncBusy&&File.ReadAllText(FileFor(target)).Contains("0.7"));
        check("应用共享音量并保存按键例外",service.Member(target)!.IndependentKeys.Contains("key_key.jump")&&model.HasSettingsSyncBackup);
        File.WriteAllText(FileFor(source),File.ReadAllText(FileFor(source)).Replace("0.7","0.8"));Wait(model.PrepareSharedSettings!(target,CancellationToken.None));
        check("下次启动前继续同步新音量并保留独立按键画面",File.ReadAllText(FileFor(target)).Contains("0.8")&&File.ReadAllText(FileFor(target)).Contains("key.keyboard.w")&&File.ReadAllText(FileFor(target)).Contains("renderDistance:8"));
        File.WriteAllText(FileFor(source),File.ReadAllText(FileFor(source)).Replace("0.8","0.9"));model.GameExited?.Invoke(model.GameRoot,source);Until(()=>service.Read()?.Options.GetValueOrDefault("soundCategory_master")=="0.9");Wait(model.PrepareSharedSettings!(target,CancellationToken.None));
        check("来源游戏退出后刷新共享方案",File.ReadAllText(FileFor(target)).Contains("0.9"));
        model.RestoreSettingsSyncCommand.Execute(null);model.ConfirmOkCommand.Execute(null);Until(()=>!model.SettingsSyncBusy&&service.Member(target)?.Enabled==false);
        check("同步设置可恢复且暂停再次覆盖",File.ReadAllText(FileFor(target)).Contains("0.8")&&!model.FollowSettings);
        model.FollowSettings=true;model.SyncIndependentFields.Single(f=>f.Key=="soundCategory_master").Independent=true;Wait(model.SaveSettingsSyncTargetCommand.ExecuteAsync(null));
        File.WriteAllText(FileFor(source),File.ReadAllText(FileFor(source)).Replace("0.9","0.6"));Wait(model.PrepareSharedSettings!(target,CancellationToken.None));
        check("逐项独立设置在后续启动仍有效",File.ReadAllText(FileFor(target)).Contains("0.8"));
        model.DismissNoticeCommand.Execute(null);model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");pump(100);
        var sourceExpander=Find<Expander>("SettingsSyncSourceExpander");var reveal=sourceExpander.GetVisualDescendants().OfType<RevealPanel>().Single();
        model.SyncSourceEditorOpen=true;pump(35);check("展开动画在中间帧逐步显示且不缩放字体",reveal.Progress>0&&reveal.Progress<1&&reveal.Bounds.Height>0&&reveal.RenderTransform is null);
        model.SyncSourceEditorOpen=false;pump(20);model.SyncSourceEditorOpen=true;pump(260);check("快速反向展开最终状态一致",reveal.IsOpen&&reveal.Progress>0.99&&reveal.Child!.IsEffectivelyVisible);
        model.SyncSourceEditorOpen=false;pump(20);model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");pump(30);check("关闭动画立即收拢并释放内容区域",reveal.Progress==0&&reveal.Bounds.Height==0&&!reveal.Child!.IsVisible);
        var actions=0;model.ShowNotice("设置已同步",()=>actions++);pump(40);check("操作结果提示不阻塞主界面",model.HasNotice&&!model.HasOverlay&&Find<RevealPanel>("NoticeReveal").Bounds.Height>0);model.OpenNoticeCommand.Execute(null);pump(30);
        check("提示可跳转并关闭且不会重复执行",actions==1&&!model.HasNotice&&Find<RevealPanel>("NoticeReveal").Bounds.Height==0);
        model.OpenAppearanceCommand.Execute(null);check("同步页可以切回外观",model.IsAppearanceSettings&&!model.ShowsSettingsSync);
        model.ShowInstancesCommand.Execute(null);model.SelectedInstance=model.Instances.Single(i=>i.InstanceId==target);model.OpenGameCommand.Execute(null);model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="smooth");
        foreach(var tab in new[]{1,2,3,2,1,3}){model.SelectDetailTabCommand.Execute(tab.ToString());pump(15);}pump(300);
        check("详情标签快速切换后当前页仍清晰可交互",model.IsSettingsDetailTab&&Find<Button>("SaveSettingsButton").IsEffectivelyVisible&&Find<Button>("SaveSettingsButton").Opacity>0.99);
        model.SelectDetailTabCommand.Execute("2");pump(25);model.SelectedMotion=model.MotionChoices.Single(m=>m.Id=="off");pump(80);
        check("过渡中关闭动画立即显示目标详情页",model.IsBackupsDetailTab&&Find<Button>("CreateSnapshotButton").IsEffectivelyVisible);
        model.OpenSettingsSyncCommand.Execute(null);model.SyncTarget=model.SyncInstances.Single(i=>i.Id==target);window.Width=900;window.Height=640;pump(80);capture("settings-sync-compact",true);
        check("小窗口游戏同步入口保持可见",Find<Button>("SettingsSyncTab").TranslatePoint(default,window) is {X:>=0,Y:>=0});
        window.Width=1180;window.Height=800;model.DismissNoticeCommand.Execute(null);model.ShowInstancesCommand.Execute(null);pump(100);
    }
}
