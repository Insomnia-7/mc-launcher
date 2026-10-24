using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Application.Auth;
using Launcher.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Launcher.App.ViewModels;

/// <summary>Only display information is exposed to the account picker; no credentials.</summary>
public sealed record AccountChoice(string? Uuid, string Title, string Detail, bool IsCurrent)
{
    public bool IsMicrosoft => Uuid is not null;
    public override string ToString() => Title;
}

public sealed partial class MainViewModel
{
    public ObservableCollection<AccountChoice> AccountChoices { get; } = [];
    [ObservableProperty] private AccountChoice? _selectedAccountChoice;
    [ObservableProperty] private bool _isAccountsOpen;
    [ObservableProperty] private string _accountHint = "选择已保存的身份后点击「使用此身份」。";
    public bool CanManageAccounts => !IsBusy && !IsSetupBusy && !IsLoggingIn && !IsUpdating;

    private void RefreshAccountChoices(bool selectCurrent = false)
    {
        var uuid = selectCurrent || SelectedAccountChoice is null ? Account?.Uuid : SelectedAccountChoice.Uuid;
        AccountChoices.Clear();
        if (_library is null) return;
        AccountChoices.Add(new(null, "离线 · " + _library.OfflineIdentity.Name,
            "本地身份，不能进入需要正版验证的服务器", Account is null));
        foreach (var account in _library.Accounts.OrderByDescending(a =>
                     string.Equals(a.Uuid, Account?.Uuid, StringComparison.OrdinalIgnoreCase)))
            AccountChoices.Add(new(account.Uuid, account.Name + "（正版）",
                account.NeedsRefresh ? "启动时验证登录状态" : "已保存，可直接切换",
                string.Equals(Account?.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase)));
        SelectedAccountChoice = AccountChoices.FirstOrDefault(c => string.Equals(c.Uuid, uuid, StringComparison.OrdinalIgnoreCase))
            ?? AccountChoices.FirstOrDefault(c => c.IsCurrent);
        NotifyAccountActions();
    }

    partial void OnSelectedAccountChoiceChanged(AccountChoice? value) => NotifyAccountActions();
    private void NotifyAccountActions()
    {
        OnPropertyChanged(nameof(CanManageAccounts));
        OpenAccountsCommand.NotifyCanExecuteChanged(); CloseAccountsCommand.NotifyCanExecuteChanged();
        UseAccountCommand.NotifyCanExecuteChanged(); RemoveAccountCommand.NotifyCanExecuteChanged();
        LoginCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsUpdatingChanged(bool value) => NotifyAccountActions();

    [RelayCommand(CanExecute = nameof(CanManageAccountCommands))]
    private void OpenAccounts()
    {
        if (!CanManageAccounts) return;
        RefreshAccountChoices(selectCurrent: true);
        AccountHint = "选择已保存的身份后点击「使用此身份」。";
        IsAccountsOpen = true;
    }
    private bool CanManageAccountCommands() => CanManageAccounts;
    [RelayCommand(CanExecute = nameof(CanManageAccountCommands))]
    private void CloseAccounts() { if (CanManageAccounts) IsAccountsOpen = false; }
    private bool CanUseAccount() => CanManageAccounts && SelectedAccountChoice is { IsCurrent: false };
    [RelayCommand(CanExecute = nameof(CanUseAccount))]
    private void UseAccount()
    {
        if (!CanUseAccount() || SelectedAccountChoice is not { } choice || _library is null) return;
        if (choice.Uuid is { } uuid && !_library.Accounts.Any(a => a.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase)))
        { RefreshAccountChoices(selectCurrent: true); return; }
        CommitAccounts(library => library.Select(choice.Uuid), "已切换为：" + choice.Title);
    }
    private bool CanRemoveAccount() => CanManageAccounts && SelectedAccountChoice is { IsMicrosoft: true };
    [RelayCommand(CanExecute = nameof(CanRemoveAccount))]
    private void RemoveAccount()
    {
        if (!CanRemoveAccount() || SelectedAccountChoice?.Uuid is not { } uuid) return;
        var title = SelectedAccountChoice.Title;
        AskConfirm($"移除「{title}」？", "只移除这台电脑保存的登录信息，下次使用需重新登录。移除当前账号后将切换为离线身份。",
            "微软账号、游戏购买记录、其他已保存账号和游戏存档均会保留。", "移除账号", () =>
            {
                if (!CanManageAccounts) { ReportAccountStatus("请等待当前任务结束后再移除账号。"); return; }
                if (_library?.Accounts.Any(a => a.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase)) != true) return;
                if (!CommitAccounts(library => library.Remove(uuid), "已移除本地账号：" + title)) return;
                // The legacy plaintext store must not retain a removed login or migrate it back later.
                try { File.Delete(AccountStore.PathFor(AppPaths.DataDirectory)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ReportAccountStatus("账号已从当前账号簿移除，但旧版登录文件未能清理：" + ErrorText.Describe(ex));
                    _logger.LogWarning("旧版账号文件清理失败：{ErrorType}", ex.GetType().Name);
                }
            });
    }

    private void ReportAccountStatus(string message) => AccountHint = StatusText = message;
    private bool CommitAccounts(Action<AccountLibrary> change, string message)
    {
        if (_library is null) return false;
        var next = _library.Copy();
        try
        {
            change(next);
            if(next.Accounts.Count>0 && !PlatformUi.CanStoreSecrets(_secretProtector))
                throw new InvalidOperationException("此系统尚未接入安全账号存储，暂时不能保存微软登录。可以使用离线身份。");
            next.Save(AppPaths.DataDirectory, _secretProtector);
        }
        catch (Exception ex)
        {
            ReportAccountStatus("保存账号失败，当前身份保持不变：" + ErrorText.Describe(ex));
            _logger.LogWarning("保存账号失败：{ErrorType}", ex.GetType().Name);
            return false;
        }
        _library = next; Account = next.Selected;
        RefreshAccountChoices(selectCurrent: true);
        ReportAccountStatus(message);
        return true;
    }
    private bool SaveSignedInAccount(MicrosoftAccount account) =>
        CommitAccounts(library => library.AddOrUpdate(account), "已登录：" + account.Name);
}
