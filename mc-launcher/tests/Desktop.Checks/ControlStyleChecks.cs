using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Launcher.App.ViewModels;
using Launcher.Desktop;

internal static class ControlStyleChecks
{
    private static T Part<T>(Visual root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    private static Color ColorOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color ?? Colors.Transparent;
    private static Color Resource(string name) => ColorOf((IBrush)Avalonia.Application.Current!.FindResource(name)!);
    private static void Save(Visual visual, string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(visual.Bounds.Width), (int)Math.Ceiling(visual.Bounds.Height)), new Vector(96, 96));
        bitmap.Render(visual); bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
    private static double CaretOffset(TextBox field)
    {
        var presenter = Part<TextPresenter>(field, "PART_TextPresenter");
        // Read the same rectangle used by native IME/caret positioning; no production reflection.
        var rect = (Rect)typeof(TextPresenter).GetMethod("GetCursorRectangle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(presenter, null)!;
        return presenter.TranslatePoint(rect.Center, field)!.Value.Y - field.Bounds.Height / 2;
    }
    public static async Task Run(MainViewModel model, MainWindow main, string root, Func<int, Task> wait, Action<string,bool> check, bool native = false)
    {
        model.SelectedMotion = model.MotionChoices.Single(m => m.Id == "off");
        main.Width = 1280; main.Height = 820;
        var measurements = new List<object>();
        var disabledCalls = 0;
        foreach (var theme in new[] { "light", "dark" })
        {
            model.SelectThemeModeCommand.Execute(theme); model.ShowInstancesCommand.Execute(null); await wait(180);
            var search = Part<TextBox>(main, "LibrarySearch");
            foreach (var text in new[] { "", "Steve_7", "生存世界" })
            {
                search.Text = text; search.CaretIndex = text.Length; search.Focus(); await wait(40);
                var offset = CaretOffset(search);
                measurements.Add(new { theme, field = "search", text, offset });
                check($"{theme} 搜索光标居中 {text.Length}", Math.Abs(offset) <= 1.1);
            }
            search.Text = ""; await wait(30);
            var placeholder = Part<TextBlock>(search, "PART_Placeholder");
            check($"{theme} 提示文字与输入行同轴", Math.Abs(placeholder.TranslatePoint(new Rect(placeholder.Bounds.Size).Center, search)!.Value.Y - search.Bounds.Height / 2) <= 1.1);
            Save(main, Path.Combine(root, $"controls-library-{theme}.png"));
            var combo = main.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible);
            if (!native)
            {
                main.MouseMove(combo.TranslatePoint(new Point(combo.Bounds.Width/2,combo.Bounds.Height/2),main)!.Value); await wait(30);
                check($"{theme} 下拉悬停使用紫色描边与柔和底色", ColorOf(Part<Border>(combo,"Background").BorderBrush) == Resource("AccentBrush") && ColorOf(Part<Border>(combo,"Background").Background) == Resource("HoverBrush"));
            }
            combo.Focus(NavigationMethod.Tab); await wait(30);
            check($"{theme} 下拉键盘焦点采用紫色圆角", Part<Border>(combo, "HighlightBackground") is { IsVisible: true, CornerRadius.TopLeft: 8 } focus && ColorOf(focus.BorderBrush) == Resource("AccentStrongBrush"));
            combo.IsDropDownOpen = true; await wait(60);
            var popup = Part<Popup>(combo, "PART_Popup");
            var popupBorder = (Border)popup.Child!;
            check($"{theme} 下拉弹出层统一面板样式", popupBorder.CornerRadius.TopLeft == 12 && ColorOf(popupBorder.Background) == Resource("CardBrush"));
            var choice = popupBorder.GetVisualDescendants().OfType<ComboBoxItem>().First();
            check($"{theme} 下拉选项选中态可见", choice.CornerRadius.TopLeft == 8 && ColorOf(Part<ContentPresenter>(choice, "PART_ContentPresenter").Background) == Resource("SelectedBrush"));
            Save((Visual)TopLevel.GetTopLevel(choice)!, Path.Combine(root, $"controls-dropdown-{theme}.png"));
            combo.IsDropDownOpen = false;
            var account = Part<Button>(main, "HeaderAccountButton");
            var flyout = (MenuFlyout)account.Flyout!; flyout.ShowAt(account); await wait(50);
            var menu = flyout.Items.OfType<MenuItem>().First();
            var menuRoot = (Visual)TopLevel.GetTopLevel(menu)!;
            var presenter = menu.GetVisualAncestors().OfType<MenuFlyoutPresenter>().Single();
            check($"{theme} 账号菜单圆角面板", presenter.CornerRadius.TopLeft == 12 && ColorOf(presenter.Background) == Resource("CardBrush"));
            menu.IsSelected = true; await wait(20);
            check($"{theme} 账号菜单选项统一悬停高亮", menu.CornerRadius.TopLeft == 8 && menu.Bounds.Height >= 38 && ColorOf(Part<Border>(menu, "PART_LayoutRoot").Background) == Resource("HoverBrush"));
            Save(menuRoot, Path.Combine(root, $"controls-account-menu-{theme}.png"));
            menu.Focus(NavigationMethod.Directional);
            menu.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }); await wait(60);
            check($"{theme} 菜单键盘动作打开真实账号弹层", model.IsAccountsOpen);
            flyout.Hide();
            var offline = Part<TextBox>(main, "OfflineNameInput");
            foreach (var text in new[] { "", "Player_7" })
            {
                offline.Text = text; offline.CaretIndex = text.Length; offline.Focus(); await wait(40);
                var offset = CaretOffset(offline); measurements.Add(new { theme, field = "offline", text, offset });
                check($"{theme} 离线用户名光标居中 {text.Length}", Math.Abs(offset) <= 1.1);
            }
            offline.Text = ""; await wait(40); Save(main, Path.Combine(root, $"controls-offline-{theme}.png"));
            offline.Text = "Player_7"; model.CloseAccountsCommand.Execute(null); await wait(60);
        }

        // Exercise the same global styles outside a page, including native popup roots.
        var single = new TextBox { Text = "中文 / Player_7", Height = 44 };
        var multi = new TextBox { AcceptsReturn = true, Text = "邀请信息第一行\n第二行", Height = 100, TextWrapping = TextWrapping.Wrap };
        var readOnly = new TextBox { Text = "只读信息", IsReadOnly = true };
        var disabled = new TextBox { Text = "不可用", IsEnabled = false };
        var numeric = new NumericUpDown { Value = 2048, Minimum = 512, Maximum = 65536, Increment = 512, FormatString = "0" };
        var checkBox = new CheckBox { Content = "收藏 / 连续滚动" };
        var indeterminate = new CheckBox { Content = "部分选中", IsThreeState = true, IsChecked = null };
        var disabledChoice = new CheckBox { Content = "不可用", IsEnabled = false, IsChecked = true };
        var contextButton = new Button { Content = "实例更多操作" };
        var context = new ContextMenu();
        var actionable = new MenuItem { Header = "查看实例" };
        var unavailable = new MenuItem { Header = "当前不可用", IsEnabled = false, Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => disabledCalls++) };
        var parent = new MenuItem { Header = "更多选项" }; parent.Items.Add(new MenuItem { Header = "子菜单选项" });
        context.Items.Add(actionable); context.Items.Add(unavailable); context.Items.Add(new Separator()); context.Items.Add(parent);
        contextButton.ContextMenu = context;
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        foreach (var c in new Control[] { single, multi, readOnly, disabled, numeric, checkBox, indeterminate, disabledChoice, contextButton }) panel.Children.Add(c);
        var fixture = new Window { Width = 520, Height = 680, Content = panel, Title = "方隅 · 控件隔离验收" };
        fixture.Show(); await wait(100);
        foreach (var theme in new[] { "light", "dark" })
        {
            model.SelectThemeModeCommand.Execute(theme); await wait(50);
            single.Focus(); await wait(30);
            check($"{theme} 加高单行框光标仍居中", Math.Abs(CaretOffset(single)) <= 1.1);
            var multiPresenter = Part<TextPresenter>(multi, "PART_TextPresenter");
            check($"{theme} 多行输入保留顶部对齐和两行布局", multi.VerticalContentAlignment == Avalonia.Layout.VerticalAlignment.Top && multiPresenter.TextLayout.TextLines.Count >= 2 && multiPresenter.TranslatePoint(default, multi)!.Value.Y < 12);
            check($"{theme} 只读与禁用字段仍可识别", readOnly.IsReadOnly && disabled.Opacity == 0.45 && ColorOf(Part<Border>(disabled, "PART_BorderElement").Background) == Resource("FieldBrush"));
            var numericText = Part<TextBox>(numeric, "PART_TextBox"); numericText.Focus(); await wait(20);
            check($"{theme} 数字输入光标居中且无嵌套焦点框", Math.Abs(CaretOffset(numericText)) <= 1.1 && Part<Border>(numericText, "PART_BorderElement").BorderThickness.Left == 0);
            var increase = Part<RepeatButton>(numeric, "PART_IncreaseButton");
            increase.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); await wait(20);
            check($"{theme} 数字按钮仍按设定步长增加", numeric.Value == (theme == "light" ? 2560 : 3072));
            checkBox.Focus(NavigationMethod.Tab);
            checkBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
            checkBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space }); await wait(20);
            check($"{theme} 勾选框键盘切换与紫色选中反馈", checkBox.IsChecked == (theme == "light") && ColorOf(Part<Border>(checkBox, "ChoiceBox").Background) == Resource(theme == "light" ? "AccentFillBrush" : "FieldBrush"));
            if (!native)
            {
                var before = checkBox.IsChecked; var point=checkBox.TranslatePoint(new Point(checkBox.Bounds.Width/2,checkBox.Bounds.Height/2),fixture)!.Value;
                fixture.MouseMove(point);fixture.MouseDown(point,MouseButton.Left);fixture.MouseUp(point,MouseButton.Left);await wait(20);
                check($"{theme} 勾选框鼠标切换且悬停风格统一", checkBox.IsChecked == !before && ColorOf(Part<Border>(checkBox,"ChoiceSurface").Background) == Resource("HoverBrush"));
                checkBox.IsChecked = before;
            }
            check($"{theme} 半选与禁用勾选框保留状态", Part<Border>(indeterminate, "ChoiceDash").IsVisible && !Part<Avalonia.Controls.Shapes.Path>(indeterminate, "ChoiceGlyph").IsVisible && disabledChoice.Opacity == 0.45);
            Save(fixture, Path.Combine(root, $"controls-gallery-{theme}.png"));
            context.Open(contextButton); await wait(50); actionable.IsSelected = true;
            check($"{theme} 实例上下文菜单使用共用面板", context.CornerRadius.TopLeft == 12 && ColorOf(context.Background) == Resource("CardBrush"));
            check($"{theme} 禁用菜单项统一灰化", unavailable.Opacity == 0.45 && !unavailable.IsEffectivelyEnabled);
            Save((Visual)TopLevel.GetTopLevel(actionable)!, Path.Combine(root, $"controls-context-{theme}.png"));
            parent.IsSubMenuOpen = true; await wait(50);
            var child = parent.Items.OfType<MenuItem>().Single();
            check($"{theme} 子菜单保持圆角项", child.IsEffectivelyVisible && child.CornerRadius.TopLeft == 8);
            var subSurface = (Border)Part<Popup>(parent, "PART_Popup").Child!;
            check($"{theme} 子菜单面板跟随当前主题", subSurface.CornerRadius.TopLeft == 12 && ColorOf(subSurface.Background) == Resource("CardBrush"));
            parent.IsSubMenuOpen = false; context.Close();
        }
        check("禁用菜单操作没有执行", disabledCalls == 0);
        fixture.Close(); main.Activate();
        File.WriteAllText(Path.Combine(root, "control-geometry.json"), JsonSerializer.Serialize(new { native, main.RenderScaling, measurements }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
