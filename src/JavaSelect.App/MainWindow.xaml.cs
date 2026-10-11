using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JavaSelect.App.Localization;
using JavaSelect.App.UI;
using JavaSelect.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.UI.Core;
using VirtualKey = Windows.System.VirtualKey;

namespace JavaSelect.App;

public sealed partial class MainWindow : Window
{
    private readonly AppController controller;
    private Localizer text;
    private string locale, currentPage = "home";
    private bool navigating, updateInstallFlowActive;
    private readonly Dictionary<string, FrameworkElement> pages = [];
    private readonly Dictionary<string, VersionList> lists = [];
    private readonly SemaphoreSlim dialogGate = new(1);
    private TextBlock? currentName, currentHome, currentExecutable, updateStatus, quote, quoteSource;
    private Image? currentIcon;
    private StackPanel? contributorList, shortcutRows;
    private HyperlinkButton? releaseLink;
    private TextBlock? pagerLabel;
    private Button? previousPage, nextPage, checkUpdatesButton, downloadUpdateButton;
    private FrameworkElement? downloadUpdateAction;
    private TextBlock? downloadPercentage;
    private ProgressRing? scanProgress, updateProgress, quoteProgress, contributorProgress, downloadProgress;
    private int shortcutPage;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? appearanceSave;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer statusDismiss;
    private bool statusHovered;

    public MainWindow(AppController controller)
    {
        this.controller = controller; locale = controller.Settings.Language; text = new(locale);
        InitializeComponent();
        statusDismiss = DispatcherQueue.CreateTimer(); statusDismiss.IsRepeating = false;
        statusDismiss.Tick += (_, _) =>
        {
            if (!StatusBar.IsOpen) return;
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
            while (focused is not null && !ReferenceEquals(focused, StatusBar)) focused = VisualTreeHelper.GetParent(focused);
            if (controller.IsBusy || statusHovered || focused is not null) { statusDismiss.Start(); return; }
            DismissStatus();
        };
        StatusBar.PointerEntered += (_, _) => { statusHovered = true; statusDismiss.Stop(); };
        StatusBar.PointerExited += (_, _) => { statusHovered = false; if (StatusBar.IsOpen) statusDismiss.Start(); };
        StatusBar.Closing += (_, args) =>
        {
            if (statusCanClose || !animationSettings.AnimationsEnabled) return;
            args.Cancel = true; DismissStatus();
        };
        StatusBar.Closed += (_, _) => { statusDismiss.Stop(); StopStatusAnimation(); statusHiding = false; statusHovered = false; StatusBar.Visibility = Visibility.Collapsed; };
        Closed += (_, _) => { statusDismiss.Stop(); StopStatusAnimation(); appearanceSave?.Stop(); };
        ExtendsContentIntoTitleBar = true; SetTitleBar(TitleBarArea);
        if (AppWindowTitleBar.IsCustomizationSupported()) AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        try
        {
            if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
            { SystemBackdrop = new MicaBackdrop(); RootGrid.Background = new SolidColorBrush(Colors.Transparent); }
        }
        catch { }
        RootGrid.ActualThemeChanged += (_, _) => CaptionTheme();
        Activated += (_, args) => controller.IsCapturingHotkey = args.WindowActivationState != WindowActivationState.Deactivated
            && RootGrid.XamlRoot is not null && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox { Tag: "HotkeyRecorder" };
        RootGrid.SizeChanged += (_, _) =>
        {
            Navigation.PaneDisplayMode = RootGrid.ActualWidth < 960 ? NavigationViewPaneDisplayMode.LeftCompact : NavigationViewPaneDisplayMode.Left;
            StatusBar.MaxWidth = Math.Max(0, RootGrid.ActualWidth - 48);
        };
        BuildNavigation(); ApplyTheme(); Navigate("home");
        controller.Changed += (_, _) => DispatcherQueue.TryEnqueue(RefreshData);
        controller.PreferencesChanged += (_, _) => DispatcherQueue.TryEnqueue(PreferencesChanged);
        controller.StatusChanged += (_, status) => DispatcherQueue.TryEnqueue(() => ShowStatus(status));
        controller.UpdateProgressChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshUpdateControls);
    }

    private void BuildNavigation()
    {
        navigating = true; Navigation.MenuItems.Clear(); Navigation.FooterMenuItems.Clear();
        foreach (var item in new[] { ("home", "\uE80F"), ("switch", "\uE8AB"), ("appearance", "\uE790"), ("shortcuts", "\uE765"), ("general", "\uE713"), ("notifications", "\uE7F4") })
            Navigation.MenuItems.Add(new NavigationViewItem { Content = text[item.Item1], Tag = item.Item1, Icon = new FontIcon { Glyph = item.Item2 } });
        Navigation.FooterMenuItems.Add(new NavigationViewItem { Content = text["about"], Tag = "about", Icon = new FontIcon { Glyph = "\uE946" } });
        navigating = false;
    }

    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!navigating && args.SelectedItem is NavigationViewItem { Tag: string page }) ShowPage(page);
    }
    private void Navigate(string page)
    {
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().Concat(Navigation.FooterMenuItems.OfType<NavigationViewItem>()).First(x => (string)x.Tag == page);
        ShowPage(page);
    }
    private void ShowPage(string page)
    {
        if (ReferenceEquals(PageHost.Content, pages.GetValueOrDefault(page)) && currentPage == page && PageHost.Content is not null) return;
        if (!pages.TryGetValue(page, out var content))
        {
            content = page switch { "home" => Versions(false), "switch" => Versions(true), "appearance" => Appearance(), "shortcuts" => Shortcuts(), "general" => General(), "notifications" => Notifications(), _ => About() };
            pages[page] = content;
        }
        currentPage = page; PageTitle.Text = text[page]; PageSubtitle.Text = text[page + "Description"];
        PageHost.ContentTransitions = new TransitionCollection { new EntranceThemeTransition { IsStaggeringEnabled = false } };
        PageHost.Content = content; RefreshData();
        if (page == "about") _ = Run(controller.RefreshAboutAsync);
    }
    private void PreferencesChanged()
    {
        ApplyTheme();
        if (locale == controller.Settings.Language) return;
        locale = controller.Settings.Language; text = new(locale);
        pages.Clear(); lists.Clear(); currentName = currentHome = currentExecutable = quote = quoteSource = updateStatus = null;
        shortcutRows = contributorList = null; currentIcon = null; releaseLink = null;
        scanProgress = updateProgress = quoteProgress = contributorProgress = downloadProgress = null;
        checkUpdatesButton = downloadUpdateButton = null; downloadUpdateAction = null; downloadPercentage = null;
        BuildNavigation(); Navigate(currentPage);
    }
    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = controller.Settings.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        DispatcherQueue.TryEnqueue(CaptionTheme);
    }
    private void CaptionTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var title = AppWindow.TitleBar;
        title.BackgroundColor = title.InactiveBackgroundColor = title.ButtonBackgroundColor = title.ButtonInactiveBackgroundColor = Colors.Transparent;
        var foreground = (PageTitle.Foreground as SolidColorBrush)?.Color ?? (RootGrid.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black);
        title.ForegroundColor = title.ButtonForegroundColor = foreground;
        title.InactiveForegroundColor = title.ButtonInactiveForegroundColor = foreground;
        title.ButtonHoverBackgroundColor = RootGrid.ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(35, 255, 255, 255) : Windows.UI.Color.FromArgb(20, 0, 0, 0);
    }

    private sealed class VersionList
    {
        public bool Switching;
        public string Query = "", Vendor = "";
        public ObservableCollection<JavaRow> Rows = [];
        public ComboBox VendorBox = new();
        public TextBlock Count = new(), Empty = new();
        public bool Updating;
    }
    private FrameworkElement Versions(bool switching)
    {
        var page = Stack(16);
        if (switching)
        {
            currentName = Label("", 22, true); currentHome = Secondary(""); currentExecutable = Secondary(""); currentIcon = new Image { Width = 34, Height = 34, Stretch = Stretch.Uniform };
            var identity = new Grid { ColumnSpacing = 14 };
            identity.ColumnDefinitions.Add(new() { Width = new GridLength(34) }); identity.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            identity.Children.Add(currentIcon); var words = Stack(5, Secondary(text["current"]), currentName, currentHome, currentExecutable); Grid.SetColumn(words, 1); identity.Children.Add(words);
            page.Children.Add(Card(identity));
        }
        else
        {
            var actions = new WrapPanel();
            actions.Children.Add(ActionButton(text["scan"], Scan, "\uE721", true));
            scanProgress = Progress(text["scanning"]); actions.Children.Add(scanProgress);
            actions.Children.Add(ActionButton(text["manualAdd"], Add, "\uE710"));
            actions.Children.Add(ActionButton(text["goSwitch"], () => { Navigate("switch"); return Task.CompletedTask; }, "\uE8AB")); page.Children.Add(actions);
        }
        var state = new VersionList { Switching = switching };
        lists[switching ? "switch" : "home"] = state;
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(Section(text[switching ? "available" : "installed"])); heading.Children.Add(state.Count);
        state.Count.Style = (Style)RootGrid.Resources["SecondaryText"];
        var search = new AutoSuggestBox { PlaceholderText = text["search"], QueryIcon = new SymbolIcon(Symbol.Find), Width = 232 };
        AutomationProperties.SetName(search, text["search"]); search.TextChanged += (_, args) => { if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) { state.Query = search.Text; RefreshVersions(state); } };
        state.VendorBox.MinWidth = 126; state.VendorBox.MaxWidth = 165; AutomationProperties.SetName(state.VendorBox, text["vendors"]);
        state.VendorBox.SelectionChanged += (_, _) => { if (!state.Updating && state.VendorBox.SelectedItem is ComboBoxItem item) { state.Vendor = (string)item.Tag; RefreshVersions(state); } };
        var filters = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; filters.Children.Add(state.VendorBox); filters.Children.Add(search);
        page.Children.Add(Pair(heading, filters, 740));
        state.Empty.Style = (Style)RootGrid.Resources["SecondaryText"]; state.Empty.Margin = new Thickness(16, 24, 16, 24); page.Children.Add(state.Empty);
        page.Children.Add(new ItemsControl { ItemsSource = state.Rows, ItemTemplate = (DataTemplate)RootGrid.Resources["JavaRowTemplate"] });
        RefreshVersions(state); return page;
    }
    private void RefreshVersions(VersionList state)
    {
        var installations = controller.Settings.Installations;
        string Vendor(JavaInstallation java) => string.IsNullOrWhiteSpace(java.Vendor) ? text["unknownVendor"] : java.Vendor.Trim();
        var vendors = installations.Where(x => x.IsValid).Select(Vendor).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (state.Vendor != "" && !vendors.Contains(state.Vendor, StringComparer.OrdinalIgnoreCase)) state.Vendor = "";
        var existingVendors = state.VendorBox.Items.OfType<ComboBoxItem>().Select(x => (string)x.Tag).ToArray();
        if (!existingVendors.SequenceEqual(new[] { "" }.Concat(vendors)))
        {
            state.Updating = true; state.VendorBox.Items.Clear(); state.VendorBox.Items.Add(new ComboBoxItem { Content = text["vendors"], Tag = "" });
            foreach (var vendor in vendors) state.VendorBox.Items.Add(new ComboBoxItem { Content = vendor, Tag = vendor });
            state.VendorBox.SelectedItem = state.VendorBox.Items.OfType<ComboBoxItem>().First(x => (string)x.Tag == state.Vendor); state.Updating = false;
        }
        var terms = Normalize(state.Query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var found = installations.Where(x => (state.Vendor == "" || string.Equals(Vendor(x), state.Vendor, StringComparison.OrdinalIgnoreCase))
            && terms.All(t => Normalize($"{x.DisplayName} {x.Version} {x.Vendor} {x.Path} Java{x.MajorVersion} Java {x.MajorVersion} {x.Kind}{x.MajorVersion}").Contains(t))).ToList();
        for (var i = state.Rows.Count - 1; i >= 0; i--) if (!found.Any(x => x.Id == state.Rows[i].Id)) state.Rows.RemoveAt(i);
        for (var i = 0; i < found.Count; i++)
        {
            var java = found[i]; var row = state.Rows.FirstOrDefault(x => x.Id == java.Id);
            if (row is null) { row = new JavaRow(state.Switching, value => Run(() => controller.SwitchAsync(value.Id)), value => Run(() => Edit(value)), value => Run(() => Remove(value))); state.Rows.Insert(i, row); }
            else if (state.Rows.IndexOf(row) != i) state.Rows.Move(state.Rows.IndexOf(row), i);
            row.Update(java, controller.CurrentSelectedId, controller.Settings.LastSelectedId, text);
        }
        state.Count.Text = terms.Length > 0 || state.Vendor != "" ? $"{found.Count} / {installations.Count}" : installations.Count.ToString();
        state.Empty.Text = text[installations.Count == 0 ? "noVersions" : "noMatches"]; state.Empty.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RefreshData()
    {
        Loading(scanProgress, controller.IsScanning);
        RefreshUpdateControls();
        Loading(quoteProgress, controller.IsLoadingAbout);
        Loading(contributorProgress, controller.IsLoadingAbout);
        foreach (var list in lists.Values) RefreshVersions(list);
        var current = controller.Settings.Installations.FirstOrDefault(x => x.Id == controller.CurrentSelectedId);
        if (currentName is not null) currentName.Text = current?.DisplayName ?? text["unselected"];
        if (currentHome is not null) currentHome.Text = "JAVA_HOME  ·  " + (string.IsNullOrEmpty(controller.CurrentJavaHome) ? "—" : controller.CurrentJavaHome);
        if (currentExecutable is not null) { currentExecutable.Text = "java.exe  ·  " + controller.EffectiveJavaPath; currentExecutable.Visibility = current is null && !string.IsNullOrEmpty(controller.EffectiveJavaPath) ? Visibility.Visible : Visibility.Collapsed; }
        if (currentIcon is not null) currentIcon.Source = current?.IconPath is { } path && File.Exists(path) ? new BitmapImage(new Uri(path)) : null;
        if (shortcutRows is not null) RefreshShortcuts();
        if (quote is not null) quote.Text = controller.QuoteText;
        if (quoteSource is not null) quoteSource.Text = controller.QuoteSource;
        if (contributorList is not null)
        {
            contributorList.Children.Clear();
            if (controller.Contributors.Count == 0 && !controller.IsLoadingAbout) contributorList.Children.Add(Secondary(text["noContributors"]));
            else { var people = new WrapPanel { Spacing = 12 }; foreach (var person in controller.Contributors) people.Children.Add(Link(person.Login, person.ProfileUrl)); contributorList.Children.Add(people); }
        }
    }

    private void RefreshUpdateControls()
    {
        Loading(updateProgress, controller.IsCheckingUpdates);
        Loading(downloadProgress, controller.IsDownloadingUpdate);
        if (checkUpdatesButton is not null) checkUpdatesButton.IsEnabled = !controller.IsCheckingUpdates && !controller.IsDownloadingUpdate && !updateInstallFlowActive;
        if (downloadUpdateAction is not null) downloadUpdateAction.Visibility = controller.CanDownloadUpdate ? Visibility.Visible : Visibility.Collapsed;
        if (downloadUpdateButton is not null)
        {
            var label = text[controller.IsUpdateReadyToInstall ? "installUpdate" : "downloadAndInstall"];
            if (!Equals(downloadUpdateButton.Tag, label)) { downloadUpdateButton.Tag = label; downloadUpdateButton.Content = IconText("\uE896", label); }
            downloadUpdateButton.IsEnabled = controller.CanDownloadUpdate && !controller.IsCheckingUpdates && !controller.IsDownloadingUpdate && !updateInstallFlowActive;
        }
        if (downloadPercentage is not null)
        {
            downloadPercentage.Text = controller.UpdateDownloadProgress is { } value ? $"{value:0}%" : "";
            downloadPercentage.Visibility = controller.IsDownloadingUpdate && controller.UpdateDownloadProgress is not null ? Visibility.Visible : Visibility.Collapsed;
        }
        if (updateStatus is not null) { updateStatus.Text = controller.UpdateStatus; updateStatus.Visibility = string.IsNullOrEmpty(controller.UpdateStatus) ? Visibility.Collapsed : Visibility.Visible; }
        if (releaseLink is not null)
        {
            releaseLink.Content = text[controller.HasUpdate && !controller.IsInstalled ? "downloadZip"
                : controller.HasUpdate && !controller.CanDownloadUpdate ? "downloadNewVersion" : "viewGitHub"];
            releaseLink.Visibility = string.IsNullOrEmpty(controller.ReleaseUrl) ? Visibility.Collapsed : Visibility.Visible;
            if (Uri.TryCreate(controller.ReleaseUrl, UriKind.Absolute, out var uri)) releaseLink.NavigateUri = uri;
        }
    }

    private async Task DownloadAndInstallUpdate()
    {
        if (updateInstallFlowActive) return;
        updateInstallFlowActive = true; RefreshUpdateControls();
        try
        {
            var installerPath = await controller.DownloadUpdateAsync();
            if (installerPath is null) return;
            var dialog = Dialog("installUpdateTitle", Label(text["installUpdateDescription"]), "quitAndInstall", compact: true);
            dialog.CloseButtonText = text["later"]; dialog.DefaultButton = ContentDialogButton.Close;
            if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) await controller.RequestUpdateInstallAsync(installerPath);
        }
        finally { updateInstallFlowActive = false; if (RootGrid.IsLoaded) RefreshUpdateControls(); }
    }

    private async Task Scan()
    {
        await controller.ScanAsync(); var candidates = controller.Candidates.Where(x => !controller.Settings.Installations.Any(v => v.Path.Equals(x.Path, StringComparison.OrdinalIgnoreCase))).ToList();
        var content = Stack(12); var checks = new List<(CheckBox Box, string Id)>();
        foreach (var java in candidates) { var box = new CheckBox { Content = Stack(4, Label(java.DisplayName, 15, true), Secondary(java.Path)), IsChecked = true }; content.Children.Add(box); checks.Add((box, java.Id)); }
        if (candidates.Count == 0)
        {
            var empty = Dialog("scanResults", Label(text["noNew"]), "ok", compact: true);
            empty.CloseButtonText = ""; await ShowDialogAsync(empty); return;
        }
        var dialog = Dialog("scanResults", content, "add");
        foreach (var check in checks) check.Box.Click += (_, _) => dialog.IsPrimaryButtonEnabled = checks.Any(x => x.Box.IsChecked == true);
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) await controller.ImportCandidatesAsync(checks.Where(x => x.Box.IsChecked == true).Select(x => x.Id));
    }
    private async Task Add()
    {
        var field = new TextBox { Header = text["path"], PlaceholderText = @"E:\JDK\jdk-21", MinWidth = 300 };
        var closed = false; var generation = 0;
        CancellationTokenSource? activeProbe = null;
        JavaInstallation? verified = null;
        string verifiedInput = ""; string? additionPath = null;
        var status = new InfoBar { IsOpen = true, IsClosable = false, Message = text["manualScanHint"], Severity = InfoBarSeverity.Informational };
        var resultName = Label("", 17, true); var resultDetails = Secondary(""); var resultPath = Secondary(""); resultPath.IsTextSelectionEnabled = true;
        var result = Card(Stack(5, resultName, resultDetails, resultPath)); result.Visibility = Visibility.Collapsed;
        var progress = Progress(text["scanning"]);
        var scan = new Button { Content = IconText("\uE721", text["scanFolder"]), IsEnabled = false };
        var browse = ActionButton(text["browse"], async () =>
        {
            var picker = new FolderPicker(AppWindow.Id);
            var folder = await picker.PickSingleFolderAsync(); if (!closed && folder is not null) field.Text = folder.Path;
        });
        var actions = new WrapPanel(); actions.Children.Add(browse); actions.Children.Add(scan); actions.Children.Add(progress);
        var dialog = Dialog("manualAdd", Stack(12, field, actions, status, result), "add"); dialog.IsPrimaryButtonEnabled = false;
        field.TextChanged += (_, _) =>
        {
            if (closed) return;
            generation++; activeProbe?.Cancel(); verified = null; verifiedInput = "";
            dialog.IsPrimaryButtonEnabled = false; result.Visibility = Visibility.Collapsed;
            Loading(progress, false); scan.IsEnabled = !string.IsNullOrWhiteSpace(field.Text);
            status.Severity = InfoBarSeverity.Informational; status.Message = text["manualScanHint"];
        };
        scan.Click += async (_, _) =>
        {
            if (closed || string.IsNullOrWhiteSpace(field.Text)) return;
            var request = ++generation; var path = field.Text.Trim(); activeProbe?.Cancel();
            var cancellation = new CancellationTokenSource(); activeProbe = cancellation;
            verified = null; verifiedInput = ""; dialog.IsPrimaryButtonEnabled = false;
            result.Visibility = Visibility.Collapsed; scan.IsEnabled = false; Loading(progress, true);
            status.Severity = InfoBarSeverity.Informational; status.Message = text["scanning"];
            try
            {
                var java = await controller.ProbeForAdditionAsync(path, cancellation.Token);
                if (closed || request != generation) return;
                if (java?.IsValid != true)
                {
                    status.Severity = InfoBarSeverity.Error; status.Message = text["javaNotDetected"]; return;
                }
                verified = java; verifiedInput = path;
                resultName.Text = java.DisplayName;
                resultDetails.Text = $"{java.Version}  ·  {java.Vendor}  ·  {java.Architecture}";
                resultPath.Text = java.Path; result.Visibility = Visibility.Visible;
                status.Severity = InfoBarSeverity.Success; status.Message = text["javaDetected"];
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested || closed) { }
            catch (Exception error)
            {
                if (closed || request != generation) return;
                status.Severity = InfoBarSeverity.Error; status.Message = text["javaScanFailed"] + "\n" + error.Message;
            }
            finally
            {
                if (ReferenceEquals(activeProbe, cancellation)) activeProbe = null;
                cancellation.Dispose();
                if (!closed && request == generation)
                {
                    Loading(progress, false); scan.IsEnabled = !string.IsNullOrWhiteSpace(field.Text);
                    DispatcherQueue.TryEnqueue(() => { if (!closed && request == generation) RevealDialogResult(dialog); });
                }
            }
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (verified is null || verifiedInput != field.Text.Trim()) { args.Cancel = true; return; }
            additionPath = verified.Path;
        };
        dialog.Closed += (_, _) => { closed = true; generation++; activeProbe?.Cancel(); };
        ContentDialogResult answer;
        try { answer = await ShowDialogAsync(dialog); }
        finally { closed = true; generation++; activeProbe?.Cancel(); }
        if (answer == ContentDialogResult.Primary && additionPath is not null) await controller.AddAsync(additionPath);
    }
    private async Task Edit(JavaInstallation java)
    {
        var alias = new TextBox { Header = text["alias"], Text = java.Alias, MaxLength = 80 };
        var originalPath = java.Path;
        var field = new TextBox { Header = text["path"], Text = originalPath };
        var showInLauncher = new ToggleSwitch { IsOn = java.ShowInLauncher, OnContent = "", OffContent = "", MinWidth = 48 };
        AutomationProperties.SetName(showInLauncher, text["showInLauncher"]);
        var visibility = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        visibility.Children.Add(Label(text["showInLauncher"], 12)); visibility.Children.Add(showInLauncher);
        var closed = false; var generation = 0;
        CancellationTokenSource? activeProbe = null;
        JavaInstallation? verified = null;
        string verifiedInput = ""; string? editedPath = null;
        bool PathUnchanged() => string.Equals(field.Text.Trim(), originalPath, StringComparison.OrdinalIgnoreCase);
        bool CanSave() => PathUnchanged() || verified is not null && verifiedInput == field.Text.Trim();
        var status = new InfoBar { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
        var resultName = Label("", 14, true); var resultDetails = Secondary(""); var resultPath = Secondary(""); resultPath.IsTextSelectionEnabled = true;
        var result = Stack(3, resultName, resultDetails, resultPath); result.Visibility = Visibility.Collapsed;
        var progress = Progress(text["scanning"]);
        var scan = new Button { Content = IconText("\uE721", text["scanFolder"]), IsEnabled = !string.IsNullOrWhiteSpace(originalPath) };
        var browse = ActionButton(text["browse"], async () =>
        {
            var picker = new FolderPicker(AppWindow.Id);
            var folder = await picker.PickSingleFolderAsync(); if (!closed && folder is not null) field.Text = folder.Path;
        });
        var actions = new WrapPanel(); actions.Children.Add(browse); actions.Children.Add(scan); actions.Children.Add(progress); actions.Children.Add(visibility);
        var dialog = Dialog("edit", Stack(6, alias, field, actions, status, result), "save");
        field.TextChanged += (_, _) =>
        {
            if (closed) return;
            generation++; activeProbe?.Cancel(); verified = null; verifiedInput = "";
            dialog.IsPrimaryButtonEnabled = PathUnchanged(); result.Visibility = Visibility.Collapsed; status.IsOpen = false;
            Loading(progress, false); scan.IsEnabled = !string.IsNullOrWhiteSpace(field.Text);
        };
        scan.Click += async (_, _) =>
        {
            if (closed || string.IsNullOrWhiteSpace(field.Text)) return;
            var request = ++generation; var path = field.Text.Trim(); activeProbe?.Cancel();
            var cancellation = new CancellationTokenSource(); activeProbe = cancellation;
            verified = null; verifiedInput = ""; dialog.IsPrimaryButtonEnabled = false;
            result.Visibility = Visibility.Collapsed; status.IsOpen = false; scan.IsEnabled = false; Loading(progress, true);
            try
            {
                var detected = await controller.ProbeForAdditionAsync(path, cancellation.Token);
                if (closed || request != generation) return;
                if (detected?.IsValid != true) { status.IsOpen = true; status.Message = text["javaNotDetected"]; return; }
                verified = detected; verifiedInput = path;
                resultName.Text = detected.DisplayName;
                resultDetails.Text = $"{detected.Version}  ·  {detected.Vendor}  ·  {detected.Architecture}";
                resultPath.Text = detected.Path; result.Visibility = Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested || closed) { }
            catch (Exception error)
            {
                if (closed || request != generation) return;
                status.IsOpen = true; status.Message = text["javaScanFailed"] + "\n" + error.Message;
            }
            finally
            {
                if (ReferenceEquals(activeProbe, cancellation)) activeProbe = null;
                cancellation.Dispose();
                if (!closed && request == generation)
                {
                    Loading(progress, false); scan.IsEnabled = !string.IsNullOrWhiteSpace(field.Text);
                    dialog.IsPrimaryButtonEnabled = CanSave();
                    DispatcherQueue.TryEnqueue(() => { if (!closed && request == generation) RevealDialogResult(dialog); });
                }
            }
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!CanSave()) { args.Cancel = true; return; }
            editedPath = PathUnchanged() ? originalPath : verified!.Path;
        };
        dialog.Closed += (_, _) => { closed = true; generation++; activeProbe?.Cancel(); };
        ContentDialogResult answer;
        try { answer = await ShowDialogAsync(dialog); }
        finally { closed = true; generation++; activeProbe?.Cancel(); }
        if (answer == ContentDialogResult.Primary && editedPath is not null)
        {
            await controller.UpdateInstallationAsync(java.Id, editedPath, alias.Text.Trim(), showInLauncher.IsOn);
            RefreshData();
        }
    }
    private async Task Remove(JavaInstallation java)
    {
        if (java.Id == controller.Settings.LastSelectedId) return;
        if (await ShowDialogAsync(Dialog("removeQuestion", Stack(10, Label(java.DisplayName, 17, true), Secondary(text["removeDescription"])), "remove")) == ContentDialogResult.Primary) await controller.RemoveAsync(java.Id);
    }

    private FrameworkElement Appearance()
    {
        var settings = controller.Settings; var page = Stack(16); var themes = new WrapPanel(); var themeButtons = new List<(ToggleButton Button, string Theme)>();
        foreach (var entry in new[] { ("system", "\uE770"), ("light", "\uE706"), ("dark", "\uE708") })
        {
            var button = new ToggleButton { Content = IconText(entry.Item2, text[entry.Item1]), IsChecked = settings.Theme == entry.Item1, Padding = new Thickness(16, 10, 16, 10), MinWidth = 132 };
            themeButtons.Add((button, entry.Item1)); themes.Children.Add(button);
            button.Click += async (_, _) => { settings.Theme = entry.Item1; await Run(controller.SaveAsync); foreach (var item in themeButtons) item.Button.IsChecked = item.Theme == settings.Theme; ApplyTheme(); };
        }
        page.Children.Add(Section(text["theme"])); page.Children.Add(Card(themes));
        page.Children.Add(Section(text["backgroundAndText"]));
        var body = new List<FrameworkElement>();
        var solidRow = Stack(); var imageRow = Stack(); var imageBlurToggleRow = Stack(); var imageBlurRow = Stack();
        void BackgroundVisibility()
        {
            solidRow.Visibility = settings.OverlayBackgroundStyle == "solid" ? Visibility.Visible : Visibility.Collapsed;
            imageRow.Visibility = imageBlurToggleRow.Visibility = settings.OverlayBackgroundStyle == "image" ? Visibility.Visible : Visibility.Collapsed;
            imageBlurRow.Visibility = settings.OverlayBackgroundStyle == "image" && settings.OverlayImageBlurEnabled ? Visibility.Visible : Visibility.Collapsed;
        }
        body.Add(Setting("background", Choice(settings.OverlayBackgroundStyle, [("frosted", "frosted"), ("solid", "solid"), ("image", "image")], async value => { settings.OverlayBackgroundStyle = value; BackgroundVisibility(); await controller.SaveAsync(); })));
        var output = Secondary($"{settings.OverlayOpacity:0}%");
        var slider = new Slider { Minimum = 20, Maximum = 100, StepFrequency = 1, Value = settings.OverlayOpacity, Width = 205 };
        slider.ValueChanged += (_, args) => { settings.OverlayOpacity = args.NewValue; output.Text = $"{args.NewValue:0}%"; DebounceSave(); };
        AutomationProperties.SetName(slider, text["opacity"]); body.Add(Setting("opacity", Stack(0, output, slider)));
        var radiusOutput = Secondary($"{settings.OverlayCornerRadius:0} px");
        var radiusSlider = new Slider { Minimum = 0, Maximum = 40, StepFrequency = 1, Value = settings.OverlayCornerRadius, Width = 205 };
        radiusSlider.ValueChanged += (_, args) =>
        {
            settings.OverlayCornerRadius = args.NewValue; radiusOutput.Text = $"{args.NewValue:0} px"; DebounceSave();
        };
        AutomationProperties.SetName(radiusSlider, text["cornerRadius"]);
        body.Add(Setting("cornerRadius", Stack(0, radiusOutput, radiusSlider)));
        FrameworkElement SelectionColorSetting(string key, Func<string> read, Action<string> write, Func<Windows.UI.Color> automaticColor)
        {
            Windows.UI.Color CurrentColor() => read() == "auto" ? automaticColor() : ParseColor(read());
            var colorPicker = new ColorPicker { IsAlphaEnabled = false, IsColorSliderVisible = true, MaxWidth = 300, Color = CurrentColor() };
            var flyout = new Flyout { Content = colorPicker };
            var swatch = new Border { Width = 24, Height = 18, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(colorPicker.Color) };
            var label = Label(read() == "auto" ? text["auto"] : read());
            var button = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { swatch, label } }, Flyout = flyout };
            AutomationProperties.SetName(button, text[key]);
            var automatic = new Button { Content = text["auto"], IsEnabled = read() != "auto" };
            var changed = false;
            void RefreshColor()
            {
                colorPicker.Color = CurrentColor(); swatch.Background = new SolidColorBrush(colorPicker.Color);
                label.Text = read() == "auto" ? text["auto"] : read(); automatic.IsEnabled = read() != "auto";
            }
            flyout.Opened += (_, _) => { RefreshColor(); changed = false; };
            colorPicker.ColorChanged += (_, args) =>
            {
                changed = true; swatch.Background = new SolidColorBrush(args.NewColor);
                label.Text = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
            };
            flyout.Closed += async (_, _) =>
            {
                if (!changed) return;
                write($"#{colorPicker.Color.R:X2}{colorPicker.Color.G:X2}{colorPicker.Color.B:X2}");
                await Run(controller.SaveAsync); RefreshColor();
            };
            automatic.Click += async (_, _) => { write("auto"); await Run(controller.SaveAsync); RefreshColor(); };
            var actions = new WrapPanel(); actions.Children.Add(button); actions.Children.Add(automatic);
            return Setting(key, actions);
        }
        body.Add(SelectionColorSetting("selectionColor", () => settings.OverlaySelectionColor, value => settings.OverlaySelectionColor = value,
            () => new Windows.UI.ViewManagement.UISettings().GetColorValue(RootGrid.ActualTheme == ElementTheme.Dark
                ? Windows.UI.ViewManagement.UIColorType.AccentLight2 : Windows.UI.ViewManagement.UIColorType.AccentDark2)));
        body.Add(SelectionColorSetting("selectionBackgroundColor", () => settings.OverlaySelectionBackgroundColor, value => settings.OverlaySelectionBackgroundColor = value,
            () => ParseColor(RootGrid.ActualTheme == ElementTheme.Dark ? "#202124" : "#F7F9FC")));
        var picker = new ColorPicker { IsAlphaEnabled = false, IsColorSliderVisible = true, MaxWidth = 300, Color = ParseColor(settings.OverlaySolidColor) };
        var colorFlyout = new Flyout { Content = picker }; var colorBox = new Border { Width = 24, Height = 18, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(picker.Color) };
        var colorButton = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { colorBox, Label(settings.OverlaySolidColor) } }, Flyout = colorFlyout };
        picker.ColorChanged += (_, e) => { colorBox.Background = new SolidColorBrush(e.NewColor); ((TextBlock)((StackPanel)colorButton.Content).Children[1]).Text = $"#{e.NewColor.R:x2}{e.NewColor.G:x2}{e.NewColor.B:x2}"; };
        colorFlyout.Closed += async (_, _) => { settings.OverlaySolidColor = $"#{picker.Color.R:x2}{picker.Color.G:x2}{picker.Color.B:x2}"; await Run(controller.SaveAsync); };
        solidRow.Children.Add(Setting("backgroundColor", colorButton)); body.Add(solidRow);
        var imageName = Secondary(settings.OverlayImageName ?? text["noImage"]); var imageActions = new WrapPanel();
        imageActions.Children.Add(ActionButton(text["chooseImage"], async () => { await controller.PickBackgroundAsync(WinRT.Interop.WindowNative.GetWindowHandle(this)); imageName.Text = settings.OverlayImageName ?? text["noImage"]; }));
        imageActions.Children.Add(ActionButton(text["removeImage"], async () => { await controller.RemoveBackgroundAsync(); imageName.Text = text["noImage"]; BackgroundVisibility(); }));
        imageRow.Children.Add(Setting("image", Stack(8, imageName, imageActions))); body.Add(imageRow);
        imageBlurToggleRow.Children.Add(Toggle("enableImageBlur", settings.OverlayImageBlurEnabled, value =>
        {
            settings.OverlayImageBlurEnabled = value; BackgroundVisibility();
        }));
        body.Add(imageBlurToggleRow);
        var imageBlurOutput = Secondary($"{settings.OverlayImageBlur:0} px");
        var imageBlurSlider = new Slider { Minimum = 0, Maximum = 30, StepFrequency = 1, Value = settings.OverlayImageBlur, Width = 205 };
        AutomationProperties.SetName(imageBlurSlider, text["imageBlur"]);
        imageBlurSlider.ValueChanged += (_, args) =>
        {
            settings.OverlayImageBlur = args.NewValue; imageBlurOutput.Text = $"{args.NewValue:0} px"; DebounceSave();
        };
        imageBlurRow.Children.Add(Setting("imageBlur", Stack(0, imageBlurOutput, imageBlurSlider))); body.Add(imageBlurRow); BackgroundVisibility();
        body.Add(Setting("textColor", Choice(settings.OverlayTextColor, [("auto", "auto"), ("black", "black"), ("white", "white")], async value => { settings.OverlayTextColor = value; await controller.SaveAsync(); })));
        page.Children.Add(Group(body.ToArray()));
        page.Children.Add(Section(text["textBackground"]));
        var textPicker = new ColorPicker { IsAlphaEnabled = false, IsColorSliderVisible = true, MaxWidth = 300, Color = ParseColor(settings.OverlayTextBackgroundColor) };
        var textColorFlyout = new Flyout { Content = textPicker };
        var textColorSwatch = new Border { Width = 24, Height = 18, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(textPicker.Color) };
        var textColorLabel = Label(settings.OverlayTextBackgroundColor);
        var textColorButton = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { textColorSwatch, textColorLabel } }, Flyout = textColorFlyout };
        AutomationProperties.SetName(textColorButton, text["textBackgroundColor"]);
        textPicker.ColorChanged += (_, args) =>
        {
            textColorSwatch.Background = new SolidColorBrush(args.NewColor);
            textColorLabel.Text = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
        };
        textColorFlyout.Closed += async (_, _) =>
        {
            settings.OverlayTextBackgroundColor = $"#{textPicker.Color.R:X2}{textPicker.Color.G:X2}{textPicker.Color.B:X2}";
            await Run(controller.SaveAsync);
        };
        var textRadiusOutput = Secondary($"{settings.OverlayTextBackgroundCornerRadius:0} px");
        var textRadiusSlider = new Slider { Minimum = 0, Maximum = 40, StepFrequency = 1, Value = settings.OverlayTextBackgroundCornerRadius, Width = 205 };
        AutomationProperties.SetName(textRadiusSlider, text["textBackgroundCornerRadius"]);
        textRadiusSlider.ValueChanged += (_, args) =>
        {
            settings.OverlayTextBackgroundCornerRadius = args.NewValue; textRadiusOutput.Text = $"{args.NewValue:0} px"; DebounceSave();
        };
        var textTransparencyOutput = Secondary($"{100 - settings.OverlayTextBackgroundOpacity:0}%");
        var textTransparencySlider = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, Value = 100 - settings.OverlayTextBackgroundOpacity, Width = 205 };
        AutomationProperties.SetName(textTransparencySlider, text["textBackgroundTransparency"]);
        textTransparencySlider.ValueChanged += (_, args) =>
        {
            settings.OverlayTextBackgroundOpacity = 100 - args.NewValue; textTransparencyOutput.Text = $"{args.NewValue:0}%"; DebounceSave();
        };
        var textColorRow = Setting("textBackgroundColor", textColorButton);
        var textRadiusRow = Setting("textBackgroundCornerRadius", Stack(0, textRadiusOutput, textRadiusSlider));
        var textTransparencyRow = Setting("textBackgroundTransparency", Stack(0, textTransparencyOutput, textTransparencySlider));
        void TextBackgroundVisibility() => textColorRow.Visibility = textRadiusRow.Visibility = textTransparencyRow.Visibility = settings.OverlayTextBackground ? Visibility.Visible : Visibility.Collapsed;
        TextBackgroundVisibility();
        page.Children.Add(Group(Toggle("enableTextBackground", settings.OverlayTextBackground, value => { settings.OverlayTextBackground = value; TextBackgroundVisibility(); }),
            textColorRow, textRadiusRow, textTransparencyRow));
        page.Children.Add(ActionButton(text["resetAppearance"], async () =>
        {
            appearanceSave?.Stop();
            try { await controller.ResetAppearanceAsync(); }
            finally
            {
                ApplyTheme(); pages.Remove("appearance");
                if (currentPage == "appearance") ShowPage("appearance");
            }
            ShowStatus(text["appearanceReset"]);
        }, "\uE777"));
        return page;
    }
    private FrameworkElement Shortcuts()
    {
        var settings = controller.Settings; var page = Stack(16);
        page.Children.Add(Section(text["switcherSettings"]));
        page.Children.Add(Group(
            Setting("globalHotkey", Hotkey(settings.OverlayHotkey, value => SaveString(() => settings.OverlayHotkey, v => settings.OverlayHotkey = v, value), false)),
            Setting("switchMode", Choice(settings.SwitchMode, [("instant", "instant"), ("confirm", "confirm")], async value => { settings.SwitchMode = value; await controller.SaveAsync(); })),
            Toggle("wrap", settings.WrapSelection, value => settings.WrapSelection = value)));
        page.Children.Add(Section(text["directShortcuts"])); shortcutRows = Stack(8);
        shortcutRows.ChildrenTransitions = new TransitionCollection { new EntranceThemeTransition { IsStaggeringEnabled = false } };
        page.Children.Add(shortcutRows);
        previousPage = new Button { Content = text["previous"] };
        previousPage.Click += (_, _) => { shortcutPage--; RefreshShortcuts(); };
        nextPage = new Button { Content = text["next"] };
        nextPage.Click += (_, _) => { shortcutPage++; RefreshShortcuts(); }; pagerLabel = Secondary("");
        var pager = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; pager.Children.Add(previousPage); pager.Children.Add(pagerLabel); pager.Children.Add(nextPage); page.Children.Add(pager); RefreshShortcuts(); return page;
    }
    private void RefreshShortcuts()
    {
        if (shortcutRows is null) return;
        var versions = controller.Settings.Installations; var pagesCount = Math.Max(1, (versions.Count + 2) / 3); shortcutPage = Math.Clamp(shortcutPage, 0, pagesCount - 1);
        shortcutRows.Children.Clear();
        foreach (var java in versions.Skip(shortcutPage * 3).Take(3))
            shortcutRows.Children.Add(Card(Pair(Stack(4, Label(java.DisplayName, 15, true), Secondary(java.Path)), Hotkey(java.Hotkey, value => SaveString(() => java.Hotkey, v => java.Hotkey = v, value)), 700)));
        if (versions.Count == 0) shortcutRows.Children.Add(Card(Secondary(text["noVersions"])));
        if (pagerLabel is not null) pagerLabel.Text = $"{shortcutPage + 1} / {pagesCount}";
        if (previousPage is not null) previousPage.IsEnabled = shortcutPage > 0;
        if (nextPage is not null) nextPage.IsEnabled = shortcutPage < pagesCount - 1;
    }
    private FrameworkElement General()
    {
        var settings = controller.Settings; var page = Stack(16);
        var languages = new ComboBox { MinWidth = 190 };
        foreach (var entry in new[] { ("zh-CN", "简体中文"), ("zh-TW", "繁體中文"), ("en-US", "English"), ("ja-JP", "日本語") }) languages.Items.Add(new ComboBoxItem { Content = entry.Item2, Tag = entry.Item1 });
        languages.SelectedItem = languages.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == settings.Language);
        languages.SelectionChanged += async (_, _) => { if (languages.SelectedItem is ComboBoxItem item) { settings.Language = (string)item.Tag; await Run(controller.SaveAsync); } };
        page.Children.Add(Group(Setting("language", languages)));
        page.Children.Add(Section(text["startupBehavior"]));
        page.Children.Add(Group(Toggle("startup", settings.StartAtLogin, v => settings.StartAtLogin = v), Toggle("startInTray", settings.StartInTray, v => settings.StartInTray = v), Toggle("closeToTray", settings.CloseToTray, v => settings.CloseToTray = v)));
        page.Children.Add(Section(text["javaEnvironment"]));
        var title = new WrapPanel { VerticalAlignment = VerticalAlignment.Center }; title.Children.Add(Label(text["terminalSync"]));
        var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 3, 7, 3), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 231, 159, 0)), Child = Label("⚠ " + text["experimental"], 11) }; title.Children.Add(badge);
        var help = new Button { Content = "?", Width = 26, Height = 26, Padding = new Thickness(0), Flyout = new Flyout { Content = new TextBlock { Text = text["terminalHelp"], TextWrapping = TextWrapping.Wrap, MaxWidth = 370 } } };
        ToolTipService.SetToolTip(help, text["terminalHelp"]); title.Children.Add(help);
        page.Children.Add(Group(Toggle("protect", settings.KeepCurrentJavaSelection, v => settings.KeepCurrentJavaSelection = v, "protectDescription"),
            Pair(title, ToggleControl(settings.AutomaticTerminalSync, v => settings.AutomaticTerminalSync = v, text["terminalSync"]), 680)));
        page.Children.Add(ActionButton(text["configFolder"], () => { controller.OpenConfigurationFolder(); return Task.CompletedTask; }, "\uE8B7")); return page;
    }
    private FrameworkElement Notifications()
    {
        var settings = controller.Settings;
        var actions = new WrapPanel();
        actions.Children.Add(ActionButton(text["testNotice"], controller.TestNotificationAsync, "\uE91C"));
        actions.Children.Add(ActionButton(text["windowsNoticeSettings"], () => { AppController.OpenLink("ms-settings:notifications"); return Task.CompletedTask; }, "\uE713"));
        return Stack(16, Group(Toggle("switchNotice", settings.SwitchNotifications, v => settings.SwitchNotifications = v), Toggle("discoveryNotice", settings.DiscoveryNotifications, v => settings.DiscoveryNotifications = v), Toggle("restoreNotice", settings.RestoreNotifications, v => settings.RestoreNotifications = v)), actions);
    }
    private FrameworkElement About()
    {
        var page = Stack(16);
        var repositoryLink = Link(text["repository"], RepositoryUrl);
        repositoryLink.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { (UIElement)((DataTemplate)RootGrid.Resources["GitHubIconTemplate"]).LoadContent(), Label(text["repository"]), new FontIcon { Glyph = "\uE72D", FontSize = 14 } }
        };
        AutomationProperties.SetName(repositoryLink, text["repository"]);
        var product = new Grid { RowSpacing = 14 };
        product.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        product.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        product.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var productHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Top };
        productHeader.Children.Add(Image("Assets/icon.png", 48));
        productHeader.Children.Add(Stack(4, Label("JavaSelect", 22, true), Secondary("v" + controller.AppVersion)));
        product.Children.Add(productHeader);
        var description = Label(text["description"], 13); Grid.SetRow(description, 1); product.Children.Add(description);
        Grid.SetRow(repositoryLink, 2); product.Children.Add(repositoryLink);
        Border AboutFrame(UIElement content, string name, string id)
        {
            var frame = new Border { Style = (Style)RootGrid.Resources["AboutCardStyle"], Child = content };
            AutomationProperties.SetName(frame, name); AutomationProperties.SetAutomationId(frame, id); return frame;
        }
        var productCard = AboutFrame(new Border { Padding = new Thickness(18), Child = product }, "JavaSelect", "AboutProductCard");
        var avatar = new AvatarPortrait(Path.Combine(AppContext.BaseDirectory, "Assets/avatar.png"), double.NaN, double.NaN, fadeRight: true);
        quote = Label(controller.QuoteText, 13); quote.MaxLines = 3; quote.TextTrimming = TextTrimming.CharacterEllipsis;
        quote.SetBinding(ToolTipService.ToolTipProperty, new Binding { Source = quote, Path = new PropertyPath("Text") });
        quoteSource = Secondary(controller.QuoteSource); quoteSource.MaxLines = 1; quoteSource.TextTrimming = TextTrimming.CharacterEllipsis;
        quoteProgress = Progress(text["loadingQuote"]);
        var developerText = Stack(12, Stack(4, Label("霄染", 22, true), Secondary(text["developer"])), Label(text["starRequest"], 13),
            new Border { Style = (Style)RootGrid.Resources["DividerStyle"] }, Stack(6, quoteProgress, quote, quoteSource));
        var developer = new Grid();
        developer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38, GridUnitType.Star) });
        developer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62, GridUnitType.Star) });
        developer.Children.Add(avatar);
        var information = new Border { Padding = new Thickness(12, 18, 18, 18), Child = developerText };
        Grid.SetColumn(information, 1); developer.Children.Add(information);
        var developerCard = AboutFrame(developer, text["developer"], "AboutDeveloperCard");
        var aboutCards = new Grid { ColumnSpacing = 18, UseLayoutRounding = true };
        aboutCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        aboutCards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        aboutCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        aboutCards.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        aboutCards.Children.Add(productCard); Grid.SetColumn(developerCard, 1); aboutCards.Children.Add(developerCard);
        void SizeAboutCards()
        {
            var availableWidth = Math.Max(0, PageScroll.ViewportWidth - PageHost.Margin.Left - PageHost.Margin.Right);
            if (availableWidth <= 0) return;
            aboutCards.Width = availableWidth;
            var scale = aboutCards.XamlRoot?.RasterizationScale ?? 1;
            var widthPixels = Math.Floor(availableWidth * scale);
            var stacked = availableWidth < 650;
            var gapPixels = stacked ? 0 : Math.Round(18 * scale);
            var cardWidth = (stacked ? widthPixels : Math.Floor((widthPixels - gapPixels) / 2)) / scale;
            aboutCards.ColumnSpacing = gapPixels / scale; aboutCards.RowSpacing = stacked ? 12 : 0;
            aboutCards.ColumnDefinitions[0].Width = new GridLength(cardWidth);
            aboutCards.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : cardWidth);
            aboutCards.RowDefinitions[1].Height = stacked ? GridLength.Auto : new GridLength(0);
            Grid.SetColumn(developerCard, stacked ? 0 : 1); Grid.SetRow(developerCard, stacked ? 1 : 0);
        }
        XamlRoot? cardsRoot = null;
        void CardsRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeAboutCards();
        void CardsViewportSizeChanged(object sender, SizeChangedEventArgs args) => SizeAboutCards();
        void CardsViewportChanged(object? sender, ScrollViewerViewChangedEventArgs args) => SizeAboutCards();
        aboutCards.SizeChanged += (_, _) => SizeAboutCards();
        aboutCards.Loaded += (_, _) =>
        {
            if (cardsRoot is not null) cardsRoot.Changed -= CardsRootChanged;
            cardsRoot = aboutCards.XamlRoot; if (cardsRoot is not null) cardsRoot.Changed += CardsRootChanged;
            PageScroll.SizeChanged -= CardsViewportSizeChanged; PageScroll.SizeChanged += CardsViewportSizeChanged;
            PageScroll.ViewChanged -= CardsViewportChanged; PageScroll.ViewChanged += CardsViewportChanged;
            SizeAboutCards();
        };
        aboutCards.Unloaded += (_, _) =>
        {
            if (cardsRoot is not null) cardsRoot.Changed -= CardsRootChanged; cardsRoot = null;
            PageScroll.SizeChanged -= CardsViewportSizeChanged; PageScroll.ViewChanged -= CardsViewportChanged;
        };
        page.Children.Add(aboutCards);
        static Grid ProgressAction(ProgressRing progress, Button button, TextBlock? percentage = null)
        {
            var row = new Grid { MinHeight = 32, VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            progress.Margin = new Thickness(0, 0, 8, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(progress);
            if (percentage is not null)
            {
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                percentage.Margin = new Thickness(0, 0, 8, 0); percentage.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(percentage, 1); row.Children.Add(percentage);
            }
            Grid.SetColumn(button, percentage is null ? 1 : 2); row.Children.Add(button);
            return row;
        }
        updateStatus = Secondary(controller.UpdateStatus); releaseLink = Link(text["viewGitHub"], RepositoryUrl + "/releases");
        updateProgress = Progress(text["checkingUpdates"]);
        checkUpdatesButton = ActionButton(text["checkUpdates"], controller.CheckUpdatesAsync, "\uE895");
        downloadUpdateButton = ActionButton(text["downloadAndInstall"], DownloadAndInstallUpdate, "\uE896", true);
        downloadProgress = Progress(text["downloadingUpdate"]); downloadPercentage = Secondary("");
        downloadUpdateAction = ProgressAction(downloadProgress, downloadUpdateButton, downloadPercentage);
        var updateActions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        updateActions.Children.Add(ProgressAction(updateProgress, checkUpdatesButton)); updateActions.Children.Add(downloadUpdateAction);
        page.Children.Add(Section(text["updates"]));
        page.Children.Add(Group(Pair(Stack(5, Label(text["updates"]), updateStatus, releaseLink), updateActions, 640), Toggle("autoUpdate", controller.Settings.AutoUpdate, v => controller.Settings.AutoUpdate = v)));
        contributorList = Stack(10); contributorProgress = Progress(text["loadingContributors"]);
        var contributorsLink = Link(text["viewGitHub"], RepositoryUrl + "/graphs/contributors");
        contributorsLink.MinHeight = 32; contributorsLink.VerticalAlignment = VerticalAlignment.Center;
        var contributorActions = new WrapPanel(); contributorActions.Children.Add(contributorsLink);
        contributorActions.Children.Add(ProgressAction(contributorProgress, ActionButton(text["refresh"], controller.RefreshAboutAsync, "\uE72C")));
        page.Children.Add(Card(Stack(14, Pair(Section(text["contributors"]), contributorActions, 680), contributorList)));
        page.Children.Add(Section(text["projectInfo"]));
        page.Children.Add(Group(Setting("license", Link("MIT", "https://opensource.org/license/mit")), Setting("uiDesign", Link("WinUI 3 · Fluent Design", "https://learn.microsoft.com/windows/apps/winui/winui3/"))));
        page.Children.Add(Secondary("Copyright © 2026 霄染")); return page;
    }

    private static string RepositoryUrl => ProjectInfo.RepositoryUrl;
    private async Task SaveString(Func<string> read, Action<string> write, string value) { var old = read(); write(value); try { await controller.SaveAsync(); } catch { write(old); throw; } }
    private FrameworkElement Hotkey(string value, Func<string, Task> save, bool clear = true)
    {
        var input = new TextBox { Text = value, IsReadOnly = true, PlaceholderText = text["pressShortcut"], Width = 165, Tag = "HotkeyRecorder" }; AutomationProperties.SetName(input, text["pressShortcut"]);
        input.GotFocus += (_, _) => controller.IsCapturingHotkey = true;
        input.LostFocus += (_, _) => controller.IsCapturingHotkey = false;
        input.PreviewKeyDown += async (_, args) =>
        {
            if (args.Key is VirtualKey.Tab or VirtualKey.Escape) return; args.Handled = true;
            var modifiers = new List<string>(); bool Down(VirtualKey key) => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
            if (Down(VirtualKey.Control)) modifiers.Add("Ctrl"); if (Down(VirtualKey.Menu)) modifiers.Add("Alt"); if (Down(VirtualKey.Shift)) modifiers.Add("Shift"); if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) modifiers.Add("Win");
            if (modifiers.Count == 0) return; var key = (int)args.Key;
            if (key is >= 65 and <= 90 || key is >= 48 and <= 57) modifiers.Add(((char)key).ToString()); else if (args.Key >= VirtualKey.F1 && args.Key <= VirtualKey.F24) modifiers.Add(args.Key.ToString()); else return;
            var next = string.Join('+', modifiers); await Run(async () => { await save(next); input.Text = next; });
        };
        var row = new WrapPanel(); row.Children.Add(input); if (clear) row.Children.Add(ActionButton(text["clear"], async () => { await save(""); input.Text = ""; })); return row;
    }
    private ComboBox Choice(string current, (string Value, string Label)[] values, Func<string, Task> change)
    {
        var combo = new ComboBox { MinWidth = 180, MaxWidth = 270 }; foreach (var entry in values) combo.Items.Add(new ComboBoxItem { Content = text[entry.Label], Tag = entry.Value });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == current) ?? combo.Items[0];
        combo.SelectionChanged += async (_, _) => { if (combo.SelectedItem is ComboBoxItem item) await Run(() => change((string)item.Tag)); }; return combo;
    }
    private FrameworkElement Toggle(string key, bool value, Action<bool> write, string? description = null) => Setting(key, ToggleControl(value, write, text[key]), description);
    private ToggleSwitch ToggleControl(bool value, Action<bool> write, string label)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 48 }; var reverting = false; AutomationProperties.SetName(toggle, label);
        toggle.Toggled += async (_, _) => { if (reverting) return; write(toggle.IsOn); toggle.IsEnabled = false; try { await controller.SaveAsync(); value = toggle.IsOn; } catch (Exception e) { write(value); reverting = true; toggle.IsOn = value; reverting = false; ShowStatus(e.Message, true); } finally { toggle.IsEnabled = true; } }; return toggle;
    }
    private Grid Setting(string key, FrameworkElement control, string? description = null)
    {
        var grid = Pair(description is null ? Label(text[key]) : Stack(5, Label(text[key]), Secondary(text[description])), control, 640);
        grid.MinHeight = description is null ? 32 : 48; return grid;
    }
    private static Grid Pair(FrameworkElement left, FrameworkElement right, double breakpoint, bool equal = false)
    {
        var grid = new Grid { ColumnSpacing = 18 }; grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = equal ? new GridLength(1, GridUnitType.Star) : GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(0) });
        Grid.SetColumn(right, 1); grid.Children.Add(left); grid.Children.Add(right);
        grid.SizeChanged += (_, _) =>
        {
            var narrow = grid.ActualWidth < breakpoint;
            grid.RowSpacing = narrow ? 12 : 0; grid.RowDefinitions[1].Height = narrow ? GridLength.Auto : new GridLength(0);
            Grid.SetColumn(right, narrow ? 0 : 1); Grid.SetRow(right, narrow ? 1 : 0); Grid.SetColumnSpan(left, narrow ? 2 : 1); Grid.SetColumnSpan(right, narrow ? 2 : 1);
            right.HorizontalAlignment = equal ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        }; return grid;
    }
    private Border Group(params FrameworkElement[] rows)
    {
        var panel = Stack(0);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i]; row.VerticalAlignment = VerticalAlignment.Center;
            var container = new Border { Style = (Style)RootGrid.Resources["SettingsRowStyle"], Child = row };
            container.SetBinding(UIElement.VisibilityProperty, new Binding { Source = row, Path = new PropertyPath("Visibility") });
            if (i == rows.Length - 1) container.BorderThickness = new Thickness(0);
            panel.Children.Add(container);
        }
        var card = Card(panel); card.Padding = new Thickness(0); return card;
    }
    private static ProgressRing Progress(string label)
    {
        var ring = new ProgressRing { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(ring, label); return ring;
    }
    private static void Loading(ProgressRing? ring, bool active)
    {
        if (ring is null) return; ring.IsActive = active; ring.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }
    private Border Card(UIElement content) => new() { Style = (Style)RootGrid.Resources["CardStyle"], Child = content };
    private TextBlock Section(string value) => new() { Text = value, Style = (Style)RootGrid.Resources["SectionText"] };
    private TextBlock Secondary(string value) => new() { Text = value, Style = (Style)RootGrid.Resources["SecondaryText"], VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock Label(string value, double size = 14, bool bold = false) => new() { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static StackPanel Stack(double spacing = 8, params UIElement[] children) { var panel = new StackPanel { Spacing = spacing }; foreach (var child in children) panel.Children.Add(child); return panel; }
    private static FrameworkElement IconText(string glyph, string label) => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = glyph, FontSize = 15 }, Label(label) } };
    private Button ActionButton(string label, Func<Task> action, string? glyph = null, bool accent = false)
    {
        var button = new Button { Content = glyph is null ? label : IconText(glyph, label) }; if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += async (_, _) => { button.IsEnabled = false; try { await Run(action); } finally { button.IsEnabled = true; } }; return button;
    }
    private static Image Image(string path, double size) => new() { Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, path))), Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform };
    private static HyperlinkButton Link(string label, string url) => new() { Content = label, NavigateUri = new Uri(url), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
    private ContentDialog Dialog(string title, UIElement content, string primary, bool compact = false) => new()
    {
        XamlRoot = RootGrid.XamlRoot, Title = text[title],
        Content = new ScrollViewer
        {
            Content = content, Width = Math.Min(400, Math.Max(300, RootGrid.ActualWidth - 80)),
            Height = compact ? double.NaN : Math.Min(180, Math.Max(140, RootGrid.ActualHeight - 230)),
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        },
        PrimaryButtonText = text[primary], CloseButtonText = text["cancel"],
        DefaultButton = ContentDialogButton.Primary, RequestedTheme = RootGrid.RequestedTheme
    };
    private static void RevealDialogResult(ContentDialog dialog)
    {
        if (dialog.Content is not ScrollViewer scroll) return;
        scroll.UpdateLayout();
        scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
    }
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await dialogGate.WaitAsync();
        try { dialog.RequestedTheme = RootGrid.RequestedTheme; return await dialog.ShowAsync(); }
        finally { dialogGate.Release(); }
    }
    private async Task Run(Func<Task> action) { try { await action(); } catch (Exception e) { controller.ReportError(e); ShowStatus(e.Message, true); } }
    public void ShowStatus(string message, bool error = false)
    {
        if (string.IsNullOrWhiteSpace(message)) { DismissStatus(); return; }
        var entering = StatusBar.Visibility != Visibility.Visible;
        StopStatusAnimation(); statusHiding = false;
        var preserveError = StatusBar.Message == message && StatusBar.Severity == InfoBarSeverity.Error;
        StatusBar.Message = message; StatusBar.IsOpen = true; StatusBar.Severity = error || preserveError || !string.IsNullOrWhiteSpace(controller.ConflictPath) ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        StatusBar.Visibility = Visibility.Visible; StatusBar.IsHitTestVisible = true;
        StatusBar.Opacity = 1; ((TranslateTransform)StatusBar.RenderTransform).Y = 0;
        if (entering) AnimateStatus(0, 1, 18, 0, 220, () => { });
        statusDismiss.Stop();
        statusDismiss.Interval = TimeSpan.FromSeconds(StatusBar.Severity == InfoBarSeverity.Error ? 12 : 5);
        if (StatusBar.IsOpen && !statusHovered) statusDismiss.Start();
        StatusBar.ActionButton = null;
        if (string.IsNullOrWhiteSpace(controller.ConflictPath)) { StatusBar.Content = null; return; }
        var actions = new WrapPanel();
        actions.Children.Add(ActionButton(text["repairPath"], RepairPath, "\uE73E"));
        actions.Children.Add(ActionButton(text["environmentSettings"], () => { controller.OpenEnvironmentSettings(); return Task.CompletedTask; }));
        StatusBar.Content = actions;
    }
    private async Task RepairPath()
    {
        var plan = await controller.PreparePathRepairAsync();
        if (plan is null) return;
        if (!plan.CanRepair)
        {
            var unavailable = Dialog("repairPath", Label(plan.Error ?? text["repairUnavailable"]), "ok");
            unavailable.CloseButtonText = ""; await ShowDialogAsync(unavailable); return;
        }
        var details = Stack(12, Label(text["repairDescription"]), Label(text["repairEntries"], 14, true));
        foreach (var entry in plan.RemovedEntries)
            details.Children.Add(new TextBlock { Text = entry.OriginalEntry, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 });
        details.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = text["repairWarning"] });
        details.Children.Add(Secondary(text["repairBackup"]));
        var confirmation = Dialog("repairTitle", details, "repairConfirm");
        confirmation.DefaultButton = ContentDialogButton.Close;
        if (await ShowDialogAsync(confirmation) == ContentDialogResult.Primary) await controller.RepairPathAsync(plan);
    }
    private void DebounceSave()
    {
        if (appearanceSave is null) { appearanceSave = DispatcherQueue.CreateTimer(); appearanceSave.Interval = TimeSpan.FromMilliseconds(300); appearanceSave.IsRepeating = false; appearanceSave.Tick += async (_, _) => await Run(controller.SaveAsync); } appearanceSave.Stop(); appearanceSave.Start();
    }
    private static string Normalize(string value) => value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace('/', '\\');
    private static Windows.UI.Color ParseColor(string value) => Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$") ? Windows.UI.Color.FromArgb(255, byte.Parse(value.AsSpan(1, 2), NumberStyles.HexNumber), byte.Parse(value.AsSpan(3, 2), NumberStyles.HexNumber), byte.Parse(value.AsSpan(5, 2), NumberStyles.HexNumber)) : Colors.White;
}
