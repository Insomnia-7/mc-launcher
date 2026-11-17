using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Experience;
using Launcher.Application.Install;
using Launcher.Application.Mods;

namespace Launcher.App.ViewModels;

public sealed partial class MainViewModel
{
    internal string ManagedModPath(ManagedModRow row)
    {
        if(SelectedInstance is not {} instance||!ManagedMods.Contains(row))throw new InvalidOperationException("此模组列表已变化，请刷新后再试。");
        var expected=ExperienceFiles.Child(ModChangePlans.ModsDirectory(GameRoot,instance.InstanceId),row.FileName);
        if(!string.Equals(LongPath.Canonical(row.Mod.FullPath),expected,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new InvalidDataException("模组文件不在当前实例中。");
        ExperienceFiles.RejectLinks(expected);
        if(!File.Exists(expected))throw new FileNotFoundException("模组文件已被移走，请刷新列表。",expected);
        return expected;
    }
    [RelayCommand] private async Task ShowManagedModDetailsAsync(ManagedModRow? row)
    {
        if(row is null)return;
        try
        {
            var path=ManagedModPath(row);var info=new FileInfo(path);var instance=SelectedInstance;
            var hash=await ModSourceStore.HashAsync(path);
            if(SelectedInstance!=instance||!ManagedMods.Contains(row))return;
            ManagedModDetails=$"{row.Name}\n{row.Meta}\n状态：{row.State}\n文件：{row.FileName}\n大小：{info.Length/1048576d:0.##} MB（{info.Length:N0} 字节）\n修改：{info.LastWriteTime:yyyy-MM-dd HH:mm:ss}\nSHA-1：{hash}\n位置：{path}";
        }
        catch(Exception ex){ExperienceHint=ex.Message;}
    }
    [RelayCommand] private void CloseManagedModDetails()=>ManagedModDetails="";
    internal static ProcessStartInfo ModFileLocation(string path)
    {
        if(!OperatingSystem.IsWindows())return new(Path.GetDirectoryName(path)!){UseShellExecute=true};
        var start=new ProcessStartInfo("explorer.exe"){UseShellExecute=true};start.ArgumentList.Add("/select,"+path);return start;
    }
    [RelayCommand] private void RevealManagedModFile(ManagedModRow? row)
    {
        if(row is null)return;
        try{Process.Start(ModFileLocation(ManagedModPath(row)));}catch(Exception ex){ExperienceHint=ex.Message;}
    }
    [RelayCommand] private Task ToggleManagedModAsync(ManagedModRow? row)=>ChangeManagedModAsync(row,row?.Mod.Enabled==true?ModBatchAction.Disable:ModBatchAction.Enable);
    [RelayCommand] private Task DeleteManagedModAsync(ManagedModRow? row)=>ChangeManagedModAsync(row,ModBatchAction.Delete);
    private async Task ChangeManagedModAsync(ManagedModRow? row,ModBatchAction action)
    {
        if(row is null||!CanUseModWorkbench||_modPlans is null||SelectedInstance is not {} instance)return;
        var root=GameRoot;
        try
        {
            EnsureInstanceStopped(instance.InstanceId);
            var path=ManagedModPath(row);
            var hash=await ModSourceStore.HashAsync(path);
            var plan=await _modPlans.PrepareLocalAsync(root,instance.InstanceId,action,[new(row.FileName,hash)]);
            if(GameRoot!=root||SelectedInstance!=instance)return;
            if(action==ModBatchAction.Delete)
                AskConfirm("删除模组",$"将永久删除「{row.Name}」这个模组文件。",row.FileName,"删除",()=>_ = QueueLocalPlanConfirmedAsync(plan,backup:false),danger:true);
            else await QueueModPlanAsync(plan,false);
        }
        catch(Exception ex){ExperienceHint=Launcher.Application.Diagnostics.ErrorText.Describe(ex);}
    }
}
