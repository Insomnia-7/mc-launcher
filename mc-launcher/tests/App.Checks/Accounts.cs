using System.IO;
using System.Reflection;
using System.Text;
using Launcher.App;
using Launcher.App.ViewModels;
using Launcher.Application.Abstractions;
using Launcher.Application.Auth;

internal static partial class UiCheck
{
    internal static bool FailAccountProtection => State.FailAccountSave;
    private static MicrosoftAccount FixtureAccount(char digit, string name, bool expired = false) =>
        new(new string(digit,32),name,"fixture-access-"+name,"fixture-refresh-"+name,
            expired ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddHours(12));
    private static T InvokeAccount<T>(MainViewModel vm, string method, params object[] args) =>
        (T)typeof(MainViewModel).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm,args)!;
    private static AccountLibrary SavedAccounts() => AccountLibrary.Load(AppPaths.DataDirectory,new AccountTestProtector());
    private static void Accounts()
    {
        var library = SavedAccounts(); var alpha = FixtureAccount('1',"Alpha"); var beta = FixtureAccount('2',"Beta");
        library.AddOrUpdate(alpha); library.AddOrUpdate(beta); library.SetOfflineName("Offline_01");
        library.Save(AppPaths.DataDirectory,new AccountTestProtector());
        var calls = State.AuthRequests;
        var vm = (MainViewModel)Resolve(typeof(MainViewModel))!;
        Check("已保存账号构造不发登录或续期请求", State.AuthRequests == calls && vm.Account?.Name == "Beta");
        Check("账号列表包含所有正版与离线身份", vm.AccountChoices.Count == 3 && vm.AccountChoices.Count(c=>c.IsCurrent)==1);
        Check("展示模型不包含令牌字段", typeof(AccountChoice).GetProperties().All(p=>!p.Name.Contains("Token")) && !vm.AccountChoices[1].ToString().Contains(alpha.AccessToken));
        vm.OpenAccountsCommand.Execute(null);
        Check("账号管理可从主界面打开", vm.IsAccountsOpen);
        vm.SelectedAccountChoice = vm.AccountChoices.Single(c=>c.Uuid==alpha.Uuid); vm.UseAccountCommand.Execute(null);
        Check("账号切换无需重新登录并持久保存", vm.Account?.Uuid==alpha.Uuid && SavedAccounts().Selected?.Uuid==alpha.Uuid && State.AuthRequests==calls);
        var reload = (MainViewModel)Resolve(typeof(MainViewModel))!;
        Check("重新打开启动器恢复上次选择", reload.Account?.Uuid==alpha.Uuid && reload.AccountChoices.Count==3);
        vm.OfflineName="Offline_02";
        Check("编辑离线名称不改变正版身份", vm.Account?.Uuid==alpha.Uuid && SavedAccounts().OfflineIdentity.Name=="Offline_02");
        vm.SelectedAccountChoice=vm.AccountChoices.Single(c=>c.Uuid is null); vm.UseAccountCommand.Execute(null);
        Check("主动切换离线身份并同步向导", vm.Account is null && SavedAccounts().Selected is null && vm.AccountText.Contains("Offline_02") && vm.SetupOfflineName=="Offline_02");
        State.FailAccountSave=true;
        vm.SelectedAccountChoice=vm.AccountChoices.Single(c=>c.Uuid==beta.Uuid); vm.UseAccountCommand.Execute(null);
        Check("切换保存失败保留内存与磁盘原身份", vm.Account is null && SavedAccounts().Selected is null && vm.AccountHint.Contains("保持不变"));
        State.FailAccountSave=false; vm.UseAccountCommand.Execute(null);
        Check("切换保存失败后可直接重试", vm.Account?.Uuid==beta.Uuid && SavedAccounts().Selected?.Uuid==beta.Uuid);
        State.FailLogin=true; Pump(vm.LoginCommand.ExecuteAsync(null)); State.FailLogin=false;
        Check("添加账号失败保留已有账号", vm.Account?.Uuid==beta.Uuid && vm.AccountChoices.Count==3 && !vm.IsLoggingIn && vm.LoginCommand.CanExecute(null));
        State.DelayLogin=true; var login=vm.LoginCommand.ExecuteAsync(null);
        Check("登录期间禁止切换移除与启动", vm.IsLoggingIn && !vm.CanManageAccounts && !vm.UseAccountCommand.CanExecute(null) && !vm.RemoveAccountCommand.CanExecute(null) && !vm.LaunchCommand.CanExecute(null));
        vm.LoginCancelCommand.Execute(null); Pump(login); State.DelayLogin=false;
        Check("取消添加账号恢复操作且不移除已有账号", !vm.IsLoggingIn && vm.CanManageAccounts && vm.Account?.Uuid==beta.Uuid && SavedAccounts().Accounts.Count==2);
        var gamma=FixtureAccount('3',"Gamma");
        Check("登录成功新增账号并自动选中", InvokeAccount<bool>(vm,"SaveSignedInAccount",gamma) && vm.Account?.Uuid==gamma.Uuid && vm.AccountChoices.Count==4);
        gamma=gamma with { Name="Gamma_New",RefreshToken="fixture-replacement-refresh" };
        Check("重复登录更新同 UUID 而不重复新增", InvokeAccount<bool>(vm,"SaveSignedInAccount",gamma) && vm.AccountChoices.Count==4 && SavedAccounts().Selected?.Name=="Gamma_New");
        State.FailAccountSave=true;
        Check("新增账号保存失败不丢失原账号", !InvokeAccount<bool>(vm,"SaveSignedInAccount",FixtureAccount('4',"Delta")) && vm.Account?.Uuid==gamma.Uuid && SavedAccounts().Accounts.Count==3);
        State.FailAccountSave=false;
        vm.SelectedAccountChoice=vm.AccountChoices.Single(c=>c.Uuid==alpha.Uuid);vm.RemoveAccountCommand.Execute(null);
        Check("移除账号需确认且尚未写盘", vm.IsConfirmOpen && SavedAccounts().Accounts.Count==3);
        vm.ConfirmCancelCommand.Execute(null);
        Check("取消移除保留账号和当前选择", SavedAccounts().Accounts.Count==3 && vm.Account?.Uuid==gamma.Uuid);
        File.WriteAllText(AccountStore.PathFor(AppPaths.DataDirectory), System.Text.Json.JsonSerializer.Serialize(alpha));
        vm.RemoveAccountCommand.Execute(null);vm.ConfirmOkCommand.Execute(null);
        Check("移除非当前账号保留当前身份和其他账号", vm.Account?.Uuid==gamma.Uuid && SavedAccounts().Accounts.Count==2 && SavedAccounts().Accounts.All(a=>a.Uuid!=alpha.Uuid));
        Check("移除账号同时清理旧版登录文件", !File.Exists(AccountStore.PathFor(AppPaths.DataDirectory)));
        vm.SelectedAccountChoice=vm.AccountChoices.Single(c=>c.Uuid==gamma.Uuid);vm.RemoveAccountCommand.Execute(null);
        State.FailAccountSave=true;vm.ConfirmOkCommand.Execute(null); State.FailAccountSave=false;
        Check("移除保存失败保留原账号", vm.Account?.Uuid==gamma.Uuid && SavedAccounts().Accounts.Count==2);
        vm.RemoveAccountCommand.Execute(null);vm.ConfirmOkCommand.Execute(null);
        Check("移除当前账号回到离线而不改选其他正版", vm.Account is null && SavedAccounts().Selected is null && SavedAccounts().Accounts.Single().Uuid==beta.Uuid && SavedAccounts().OfflineIdentity.Name=="Offline_02");
        Check("离线身份不能从列表移除", vm.SelectedAccountChoice?.Uuid is null && !vm.RemoveAccountCommand.CanExecute(null));
        beta=FixtureAccount('2',"Beta",expired:true); InvokeAccount<bool>(vm,"SaveSignedInAccount",beta);
        var refresh=InvokeAccount<Task<MicrosoftAccount?>>(vm,"EnsureFreshAccountAsync",CancellationToken.None); Pump(refresh);
        Check("仅续期当前账号并保存旋转后的令牌", refresh.Result?.Uuid==beta.Uuid && State.RefreshedUuid==beta.Uuid && SavedAccounts().Selected?.RefreshToken=="fixture-rotated-refresh");
        InvokeAccount<bool>(vm,"SaveSignedInAccount",beta); State.FailRefresh=true;
        var failed=false;
        try { Pump(InvokeAccount<Task<MicrosoftAccount?>>(vm,"EnsureFreshAccountAsync",CancellationToken.None)); } catch(InvalidOperationException) { failed=true; }
        State.FailRefresh=false;
        Check("续期失败不静默切换离线身份", failed && vm.Account?.Uuid==beta.Uuid && SavedAccounts().Selected?.Uuid==beta.Uuid);
        State.CancelRefresh=true;var canceled=false;
        try { Pump(InvokeAccount<Task<MicrosoftAccount?>>(vm,"EnsureFreshAccountAsync",CancellationToken.None)); } catch(OperationCanceledException) { canceled=true; }
        State.CancelRefresh=false;
        Check("续期取消向上传播并保留当前身份", canceled && vm.Account?.Uuid==beta.Uuid);
        State.WrongRefreshIdentity=true;var mismatch=false;
        try { Pump(InvokeAccount<Task<MicrosoftAccount?>>(vm,"EnsureFreshAccountAsync",CancellationToken.None)); } catch(InvalidOperationException) { mismatch=true; }
        State.WrongRefreshIdentity=false;
        Check("异常续期身份不覆盖当前账号", mismatch && SavedAccounts().Selected?.Uuid==beta.Uuid);
        vm.IsBusy=true;vm.SelectedAccountChoice=vm.AccountChoices[0];vm.UseAccountCommand.Execute(null);
        Check("启动或安装期间账号操作被阻止", !vm.CanManageAccounts && !vm.LoginCommand.CanExecute(null) && vm.Account?.Uuid==beta.Uuid);
        vm.IsBusy=false;vm.IsSetupBusy=true;
        Check("安装向导执行中禁止账号切换", !vm.OpenAccountsCommand.CanExecute(null)); vm.IsSetupBusy=false;
        vm.IsUpdating=true;Check("更新期间禁止账号切换", !vm.UseAccountCommand.CanExecute(null));vm.IsUpdating=false;
        library=SavedAccounts();
        for(var i=0;i<20;i++) library.AddOrUpdate(new MicrosoftAccount(i.ToString("x32"),"Fixture_"+i,"fixture-access","fixture-refresh",DateTimeOffset.UtcNow.AddDays(1)));
        library.Save(AppPaths.DataDirectory,new AccountTestProtector());
        var many=(MainViewModel)Resolve(typeof(MainViewModel))!;many.OpenAccountsCommand.Execute(null);
        var window=new MainWindow(many);Render(window,1000,640,"accounts-1000.png");Render(window,820,560,"accounts-820.png");window.Close();
        Check("多账号列表支持完整选择", many.AccountChoices.Count==22 && many.SelectedAccountChoice?.IsCurrent==true && many.AccountChoices[1].IsCurrent);
    }
}

/// <summary>Deterministic test protector; never used by production DI.</summary>
internal sealed class AccountTestProtector : ISecretProtector
{
    public string Describe() => "Account test protector";
    public byte[] Protect(string text)
    {
        if(UiCheck.FailAccountProtection) throw new IOException("模拟账号保存失败");
        return Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
    }
    public string? TryUnprotect(ReadOnlySpan<byte> payload)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.UTF8.GetString(payload))); }
        catch(FormatException) { return null; }
    }
}
