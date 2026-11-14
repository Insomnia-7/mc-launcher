using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Launcher.App;
using Launcher.App.ViewModels;
using Launcher.Application.Install;
using Launcher.Desktop;
using Launcher.Desktop.Controls;
using Microsoft.Extensions.DependencyInjection;

internal static class InstanceIconUiChecks
{
    public static void Run(MainViewModel model,Window window,Action<int> pump,Action<string,bool> check,Action<string,bool> capture,string root)
    {
        var kinds=new[]{("原版预览","vanilla",""),("Forge 预览","forge","net.minecraftforge:forge:1.21.4-54.0"),("Fabric 预览","fabric","net.fabricmc:fabric-loader:0.16"),("NeoForge 预览","neoforge","net.neoforged:neoforge:21.4"),("Quilt 预览","quilt","org.quiltmc:quilt-loader:0.28")};
        foreach(var (name,key,library) in kinds)
        {
            var directory=Path.Combine(model.GameRoot,"versions","custom-"+key);Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,"custom-"+key+".json"),JsonSerializer.Serialize(new{id="custom-"+key,inheritsFrom="1.21.4",mainClass="net.minecraft.client.main.Main",libraries=library.Length==0?Array.Empty<object>():new object[]{new{name=library}}}));
            new InstanceMetadata(name,null,null,null,"1.21.4",null,"custom-"+key,IsModpack:false).Write(model.GameRoot,"custom-"+key);
        }
        const string pack="art-pack";
        new InstanceMetadata("自带图标整合包预览","1.0","Modrinth","Fabric 0.16","1.21.4",null,"custom-fabric").Write(model.GameRoot,pack);
        var iconPath=Path.Combine(model.GameRoot,"instances",pack,ModpackIcon.FileName);
        var packBytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"../../../../../src/Launcher.Desktop/Assets/Loaders/neoforge.png"));
        File.WriteAllBytes(iconPath,packBytes);
        void Refresh(){check("刷新游戏库成功",model.TryUseGameRoot(model.GameRoot,out _));pump(140);}
        Refresh();model.ShowInstancesCommand.Execute(null);model.LibraryQuery="预览";pump(80);
        foreach(var (_,key,_) in kinds)
            check(key+" 自定义版本 ID 仍显示正确加载器图标",model.LibraryEntries.Single(e=>e.Instance.InstanceId=="custom-"+key).TypeIcon==key);
        var packEntry=model.LibraryEntries.Single(e=>e.Instance.InstanceId==pack);
        check("整合包自带图标优先于加载器图标且完整显示",packEntry.HasCover&&packEntry.CoverStretch==Stretch.Uniform&&packEntry.TypeIcon=="fabric");
        check("无图标纯原版整合包回退草方块",InstanceTypeIcon.KeyFor(new("plain","1.21.4","plain","","1.21.4",true))=="vanilla");
        model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="glacier");model.SetLibraryLayoutCommand.Execute("cards");pump(100);
        var scroll=window.GetVisualDescendants().OfType<ScrollViewer>().Single(c=>c.Name=="LibraryScroll");scroll.Offset=new Vector(0,315);capture("instance-icons-cards-light",true);
        model.SetLibraryLayoutCommand.Execute("list");pump(100);scroll.Offset=new Vector(0,305);capture("instance-icons-list-light",true);
                check("实际列表使用加载器原始图标控件",window.GetVisualDescendants().OfType<InstanceTypeIcon>().Count(i=>i.IsEffectivelyVisible)>=5);
        model.SelectedTheme=model.ThemeChoices.Single(t=>t.Id=="glacier");
        model.OpenGameCommand.Execute(packEntry);pump(60);
        var cover=Path.Combine(root,"user-cover.png");File.WriteAllBytes(cover,AppearanceFixture.MakeSkin());model.ImportGameCoverFileAsync(cover).GetAwaiter().GetResult();pump(60);
        check("手动封面覆盖整合包自带图标",model.HasGameCover&&model.SelectedGameCoverStretch==Stretch.UniformToFill);
        model.RemoveGameCoverCommand.Execute(null);pump(60);
        check("移除手动封面后恢复整合包自带图标",model.HasGameCover&&model.SelectedGameCoverStretch==Stretch.Uniform);
        using(var services=LauncherServices.Create())
        {var restored=services.GetRequiredService<MainViewModel>();pump(100);check("重启离线恢复整合包图标",restored.LibraryEntries.Single(e=>e.Instance.InstanceId==pack).HasCover);}
        File.WriteAllText(iconPath,"broken");Refresh();check("损坏图标自动回退加载器",!model.LibraryEntries.Single(e=>e.Instance.InstanceId==pack).HasCover&&model.LibraryEntries.Single(e=>e.Instance.InstanceId==pack).TypeIcon=="fabric");
        File.WriteAllBytes(iconPath,packBytes);Refresh();
    }
}
