using System.Text.Json;
using CowWorktrees.Services;
using CowWorktrees.ViewModels;
using CowWorktrees.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CowWorktrees;

/// <summary>
/// The walkthrough, Repository settings, moving a worktree into copy-on-write, stopping a run, updates, and
/// the app's own settings. Everything here runs the same scripts a person would run in PowerShell.
/// </summary>
public sealed partial class MainWindow
{
    private SetupWizard? _wizard;
    private RepositorySettings? _settingsView;
    private bool _closeConfirmed;
    private bool _exitWhenIdle;
    private bool _firstRefreshSeen;
    private UpdateService? _updates;
    private UpdateInfo? _update;
    private bool _updating;

    private void InitializeSetup()
    {
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) _wizard?.OnWindowActivated();
        };
        if (!_options.Hidden) AppWindow.Closing += OnAppWindowClosing;
        VM.Operations.CollectionChanged += (_, _) => ExitIfIdle();
        VM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.RunningCount)) ExitIfIdle();
        };
    }

    // ---- overlay -----------------------------------------------------------------------------------------------

    private bool OverlayOpen => OverlayHost.Visibility == Visibility.Visible;

    private void ShowOverlay(UIElement view)
    {
        OverlayHost.Children.Clear();
        OverlayHost.Children.Add(view);
        OverlayHost.Visibility = Visibility.Visible;
    }

    private void HideOverlay()
    {
        OverlayHost.Children.Clear();
        OverlayHost.Visibility = Visibility.Collapsed;
        _wizard = null;
        _settingsView = null;
    }

    // ---- the walkthrough ---------------------------------------------------------------------------------------

    private SetupWizardViewModel OpenWizard(WizardStep step = WizardStep.Welcome)
    {
        if (_wizard != null) return _wizard.VM;
        var vm = new SetupWizardViewModel(VM);
        var view = new SetupWizard(vm, this)
        {
            ConfirmCancel = async () => await ShowDialogAsync(new ContentDialog
            {
                Title = "Leave the walkthrough?",
                Content = Para("Nothing has been set up yet, and your answers are not kept."),
                PrimaryButtonText = "Leave",
                CloseButtonText = "Stay",
                DefaultButton = ContentDialogButton.Close,
            }) == ContentDialogResult.Primary,
        };
        view.CreateDevDriveRequested += () => _ = CreateDevDriveAsync();
        vm.Closed += () =>
        {
            HideOverlay();
            UpdatePages();
            SelectTab("worktrees");
        };
        vm.CreateFirstWorktree += () => DispatcherQueue.TryEnqueue(() => New_Click(this, new RoutedEventArgs()));
        _wizard = view;
        ShowOverlay(view);
        vm.GoTo(step);
        return vm;
    }

    private async void EmptyPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (VM.HasNoRepositories) OpenWizard();
        else await VM.RefreshAsync();
    }

    private void AddRepository_Click(object sender, RoutedEventArgs e) => OpenWizard(VM.Pools.Count == 0 ? WizardStep.Welcome : WizardStep.Checks);

    /// <summary>On the first start with nothing set up, the walkthrough opens by itself (once; after that the empty page offers it).</summary>
    private void OfferWalkthroughOnce()
    {
        if (_firstRefreshSeen) return;
        _firstRefreshSeen = true;
        if (_options.Wizard is { } step && !_options.Unattended)
        {
            // --wizard without a snapshot: the walkthrough opens and stays, for the UI Automation test (and for a look)
            OpenWizard(ParseStep(step));
            return;
        }
        if (_options.Hidden || _options.Unattended || !VM.HasNoRepositories || VM.Settings.WalkthroughOffered) return;
        VM.Settings.WalkthroughOffered = true;
        VM.Settings.Save();
        OpenWizard();
    }

    /// <summary>"Create one for me": scripts\new_dev_drive.ps1, elevated, in its own console. Experimental (gui.json: ExperimentalDevDrive).</summary>
    private async Task CreateDevDriveAsync()
    {
        string? script = ToolFolder.Script(VM.ToolDir, "scripts", "new_dev_drive.ps1");
        if (script == null || !File.Exists(script)) return;
        var size = new NumberBox { Header = "Size (GB)", Value = 100, Minimum = 50, Maximum = 2000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        var file = new TextBox { Header = "File that holds the drive", Text = @"C:\DevDrives\cow-worktrees.vhdx" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 520 };
        panel.Children.Add(Para("Creates a virtual disk file and formats it as a Dev Drive. The file grows as the drive fills, up to the size below. Windows asks for administrator rights once."));
        panel.Children.Add(Para("This is experimental: it has not been tested on many PCs. The Windows Settings route does the same and is the safe choice.", Theme.Caution));
        panel.Children.Add(file);
        panel.Children.Add(size);
        var dialog = new ContentDialog { Title = "Create a Dev Drive", Content = panel, PrimaryButtonText = "Create", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(CowScript.PowerShellExe) { UseShellExecute = true, Verb = "runas" };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", script, "-Path", file.Text.Trim(), "-SizeGB", ((int)size.Value).ToString() }) psi.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            VM.SetProblem($"The Dev Drive script could not be started: {ex.Message}");
        }
    }

    // ---- repository settings -----------------------------------------------------------------------------------

    private void RepositorySettings_Click(object sender, RoutedEventArgs e) => OpenRepositorySettings();

    private RepositorySettingsViewModel? OpenRepositorySettings()
    {
        if (VM.Pool is not { IsUsable: true } pool) return null;
        if (_settingsView != null) return _settingsView.VM;
        var vm = new RepositorySettingsViewModel(VM, pool);
        var view = new RepositorySettings(vm)
        {
            ConfirmDiscard = async () => await ShowDialogAsync(new ContentDialog
            {
                Title = "Leave without applying?",
                Content = Para("The changes on this page have not been applied."),
                PrimaryButtonText = "Leave",
                CloseButtonText = "Stay",
                DefaultButton = ContentDialogButton.Close,
            }) == ContentDialogResult.Primary,
        };
        view.Closed += () =>
        {
            HideOverlay();
            UpdatePages();
        };
        view.RemoveRequested += () => _ = ConfirmRemoveRepositoryAsync(pool);
        _settingsView = view;
        ShowOverlay(view);
        return vm;
    }

    private void FixDrift_Click(object sender, RoutedEventArgs e)
    {
        if (VM.Pool is not { IsUsable: true } pool || VM.SetupRunning) return;
        if (!VM.SetupAvailable || ToolFolder.RiskyLocation(VM.ToolDir) != null)
        {
            OpenRepositorySettings();   // the page says why it cannot be fixed from here
            return;
        }
        VM.StartSetup(RepositorySettingsViewModel.SavedOptions(pool, VM.Integrations), $"Repair {pool.Name}");
    }

    private async Task ConfirmRemoveRepositoryAsync(Pool pool)
    {
        if (VM.SetupRunning) return;
        var others = VM.AllRows.Where(r => r.IsCow).Select(r => r.Path).ToList();
        var alsoBase = new CheckBox { Content = "Also delete the base copy and this repository's folder on the Dev Drive" };
        var note = Para("", Theme.Secondary, 12);
        var panel = new StackPanel { Spacing = 10, MinWidth = 520 };
        panel.Children.Add(Para($"\"{pool.Name}\" is disconnected: Claude Code and Codex go back to making full copies for this project, the daily upkeep is removed, and the repository leaves this app."));
        panel.Children.Add(Para("Your project, its worktrees and its branches stay exactly as they are. You can set it up again at any time."));
        panel.Children.Add(alsoBase);
        panel.Children.Add(note);
        var command = Mono("");
        panel.Children.Add(command);
        void Update()
        {
            bool rm = alsoBase.IsChecked == true;
            note.Text = !rm ? $"The base copy stays in {pool.BasePath}, so setting the repository up again is quick."
                : others.Count == 0 ? $"The base copy in {pool.BasePath} is deleted. It can be made again by setting the repository up again (one full copy)."
                : $"{Fmt.Plural(others.Count, "worktree")} still live next to the base copy ({string.Join(", ", others.Take(3).Select(Path.GetFileName))}{(others.Count > 3 ? " …" : "")}). The base copy can only be deleted when they are gone: remove them first, or leave this box unticked.";
            note.Foreground = rm && others.Count > 0 ? Theme.Caution : Theme.Secondary;
            command.Text = SetupRunner.CommandLine(VM.UninstallScript, SetupRunner.UninstallArgs(pool.Root, rm));
        }
        alsoBase.Checked += (_, _) => Update();
        alsoBase.Unchecked += (_, _) => Update();
        Update();
        var dialog = new ContentDialog
        {
            Title = $"Remove {pool.Name} from the app?",
            Content = panel,
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        HideOverlay();
        VM.StartUninstall(pool, alsoBase.IsChecked == true);
    }

    // ---- moving a full worktree into a copy-on-write clone ------------------------------------------------------

    private async void Migrate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected != null) await ConfirmMigrateAsync(Selected);
    }

    private async Task ConfirmMigrateAsync(WorktreeViewModel row)
    {
        if (!await EnsureScriptAsync() || !row.CanMigrate || VM.Pool is not { } pool) return;
        if (VM.MigrateRunning)
        {
            SelectTab("activity");
            return;
        }

        string leaf = CowScript.SanitizeName(row.Name);
        var name = new TextBox { Header = $"Name (folder under {pool.Root})", Text = leaf.Length > 0 ? leaf : "worktree" };
        var namePreview = Para("", Theme.Secondary, 12);
        var retire = new CheckBox { Content = "When the copy has been checked, remove the old folder and leave a link in its place", IsChecked = true };
        var retireNote = Para("", Theme.Secondary, 12);
        var command = Mono("");
        var panel = new StackPanel { Spacing = 10, MinWidth = 560 };
        panel.Children.Add(Para($"{row.Path} is a full copy of the project. This makes a copy-on-write clone of the same commit and carries over everything else that is in it: changed files, untracked files and build output. The copy is then compared with the original, file by file."));
        panel.Children.Add(Para("Nothing in the old folder is changed or removed until every check has passed. If something still has the old folder open (a terminal, an editor, an AI session), it is left alone and you are told.", Theme.Secondary, 12));
        if (row.Phantom > 0) panel.Children.Add(Para(row.PhantomNote + ". That is handled.", Theme.Secondary, 12));
        if (row.LastActivity is { } t && DateTime.Now - t < TimeSpan.FromMinutes(15)) panel.Children.Add(Para($"⚠  It was active {Fmt.Relative(t)}: close whatever is working in it first.", Theme.Caution));
        panel.Children.Add(name);
        panel.Children.Add(namePreview);
        panel.Children.Add(retire);
        panel.Children.Add(retireNote);
        panel.Children.Add(command);

        var dialog = new ContentDialog
        {
            Title = $"Move {row.Name} to copy-on-write",
            Content = panel,
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        bool resumeRetire = false;   // the clone exists and was copied by an earlier run: only the retiring is left
        int check = 0;
        async void Validate()
        {
            int mine = ++check;
            var clean = CowScript.SanitizeName(name.Text);
            string target = clean.Length == 0 ? "" : Path.Combine(pool.Root, clean);
            string? problem = clean.Length == 0 ? "Enter a name." : clean.Equals("base", StringComparison.OrdinalIgnoreCase) ? "\"base\" is the base copy itself." : null;
            resumeRetire = false;
            string info = problem ?? $"→ {target}";
            if (problem == null && Directory.Exists(target))
            {
                // an earlier run of this very move leaves its mark in the clone; the script picks it up
                string from = "", state = "";
                try
                {
                    from = await VM.Git.WorktreeConfigAsync(target, "cow.migrateFrom");
                    state = await VM.Git.WorktreeConfigAsync(target, "cow.migrateState");
                }
                catch { }
                if (mine != check) return;
                bool same = from.Length > 0 && Pool.FullPath(from).Equals(row.Path, StringComparison.OrdinalIgnoreCase);
                if (same && state == "copied")
                {
                    resumeRetire = true;
                    info = $"{target} already holds a checked copy of this worktree from an earlier run. What is left is to remove the old folder.";
                }
                else if (same) info = $"{target} is from an earlier run that stopped while copying. It is picked up where it stopped.";
                else problem = $"{target} already exists. Choose another name.";
                if (problem != null) info = problem;
            }
            namePreview.Text = info;
            namePreview.Foreground = problem != null ? Theme.Critical : Theme.Secondary;
            bool willRetire = retire.IsChecked == true;
            retireNote.Text = willRetire
                ? $"The link (a junction) at {row.Path} points to the new place, so editors, terminals and sessions that remember the old path keep working. The disk space of the old copy is freed."
                : "The old folder stays, and so does the space it takes. You can finish later: choose Move to copy-on-write again on the old folder.";
            command.Text = VM.MigrateCommandLine(row.Path, clean.Length == 0 ? "<name>" : clean, resumeRetire && willRetire);
            dialog.PrimaryButtonText = resumeRetire ? "Remove the old folder" : "Move";
            dialog.IsPrimaryButtonEnabled = problem == null && (!resumeRetire || willRetire);
        }
        name.TextChanged += (_, _) => Validate();
        retire.Checked += (_, _) => Validate();
        retire.Unchecked += (_, _) => Validate();
        Validate();

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var finalName = CowScript.SanitizeName(name.Text);
        if (resumeRetire) VM.StartRetire(row.Path, finalName);
        else VM.StartMigrate(row.Path, finalName, retireAfter: retire.IsChecked == true);
    }

    // ---- stopping a run, and closing while one runs -------------------------------------------------------------

    private async void StopOperation_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedOperation is not { CanCancel: true } op) return;
        string what = op.Kind switch
        {
            OperationKind.Create => "The partly made worktree is removed again. Nothing else has changed.",
            OperationKind.Migrate => "Nothing in the old folder has been changed. The copy is kept if it had begun, and the next run picks it up where it stopped.",
            OperationKind.RefreshBase => "The base copy is already where it should be; only its build is stopped. The base has no build output until the next build finishes.",
            OperationKind.Setup => "The partly made base copy is removed again. Nothing else has changed yet.",
            _ => "",
        };
        var dialog = new ContentDialog
        {
            Title = $"Stop \"{op.Title}\"?",
            Content = Para(what),
            PrimaryButtonText = "Stop",
            CloseButtonText = "Keep running",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) op.RequestCancel();
    }

    /// <summary>
    /// Closing while a script runs: the script writes its progress to this app, so the app has to stay until it
    /// ends. The window is hidden instead, and the app exits by itself when the last run has finished.
    /// </summary>
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || VM.RunningCount == 0) return;
        args.Cancel = true;
        _placementSaved = false;
        var running = VM.Operations.Where(o => o.IsRunning).Select(o => o.Title).ToList();
        var dialog = new ContentDialog
        {
            Title = running.Count == 1 ? "A job is still running" : $"{running.Count} jobs are still running",
            Content = Para($"{string.Join(", ", running)}.\n\nYou can close the window: the work carries on in the background and finishes by itself. Open the app again to see how it went."),
            PrimaryButtonText = "Close the window",
            CloseButtonText = "Keep it open",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        if (VM.RunningCount == 0)
        {
            _closeConfirmed = true;
            Close();
            return;
        }
        _exitWhenIdle = true;
        SavePlacement();
        AppWindow.Hide();
    }

    private void ExitIfIdle()
    {
        if (!_exitWhenIdle || VM.RunningCount > 0) return;
        _exitWhenIdle = false;
        _closeConfirmed = true;
        Close();
    }

    /// <summary>The app was started again while this one runs (perhaps hidden with a job): show this window.</summary>
    public void BringToFront()
    {
        _exitWhenIdle = false;
        _placementSaved = false;
        AppWindow.Show(true);
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
    }

    // ---- check this PC, app settings and About --------------------------------------------------------------------

    private async void CheckPc_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 520 };
        var ring = new ProgressRing { IsActive = true, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(ring);
        var dialog = new ContentDialog { Title = "Check this PC", Content = panel, CloseButtonText = "Close" };
        dialog.Opened += async (_, _) =>
        {
            SystemReport report;
            try { report = await Task.Run(SystemChecks.RunAsync); }
            catch (Exception ex) { panel.Children.Add(Para(ex.Message, Theme.Critical)); return; }
            panel.Children.Remove(ring);
            foreach (var c in report.Checks)
            {
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(Para((c.IsOk ? "✔  " : "✖  ") + c.Summary, c.IsOk ? Theme.Success : c.Id == "windows" ? Theme.Caution : Theme.Critical));
                row.Children.Add(Para(c.Detail, Theme.Secondary, 12));
                panel.Children.Add(row);
            }
            panel.Children.Add(Para($"Claude Code: {(report.ClaudeFound ? "found" : "not found")}  ·  Codex: {(report.CodexFound ? "found" : "not found")}", Theme.Secondary, 12));
            if (!report.CanSetUp) panel.Children.Add(Para("The walkthrough (More, Add repository) has a button for each thing that is missing.", Theme.Secondary, 12));
        };
        await ShowDialogAsync(dialog);
    }

    private async void AppSettings_Click(object sender, RoutedEventArgs e)
    {
        string? tool = VM.ToolDir;
        bool installed = ToolFolder.IsInstalled(tool);
        var panel = new StackPanel { Spacing = 12, MinWidth = 540 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        head.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/AppIcon.png")), Width = 40, Height = 40 });
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "CoW Worktrees", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        titles.Children.Add(Para($"Version {UpdateService.CurrentVersion}", Theme.Secondary, 12));
        head.Children.Add(titles);
        panel.Children.Add(head);

        panel.Children.Add(Para(tool == null
            ? "The cow-worktrees scripts were not found next to this app."
            : $"Scripts: {tool}\n{(installed ? "Installed copy: it can update itself." : "Portable copy (from the zip): updates come from the release page.")}", Theme.Secondary, 12));
        if (ToolFolder.RiskyLocation(tool) is { } why) panel.Children.Add(Para($"⚠  This folder may not last: {why}. Install cow-worktrees, or move the folder somewhere permanent.", Theme.Caution, 12));

        var updates = new ToggleSwitch { Header = "Look for a new version once a day", IsOn = VM.Settings.CheckForUpdates, OnContent = "On", OffContent = "Off" };
        panel.Children.Add(updates);
        var checkRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var checkNow = new Button { Content = "Check now" };
        var checkResult = Para("The check only reads the latest release from GitHub; nothing is downloaded or installed without a click.", Theme.Secondary, 12);
        checkResult.MaxWidth = 400;
        checkRow.Children.Add(checkNow);
        checkRow.Children.Add(checkResult);
        panel.Children.Add(checkRow);

        var editor = new TextBox { Header = "Editor for \"Open in editor\" (empty: VS Code or Cursor when installed)", Text = VM.Settings.EditorPath, PlaceholderText = @"C:\...\Code.exe", IsSpellCheckEnabled = false };
        panel.Children.Add(editor);

        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var logs = new Button { Content = "Open the log folder" };
        logs.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(CowHome.Folder); Ui.Launch(CowHome.Folder); } catch { }
        };
        var web = new Button { Content = "Website and help" };
        web.Click += (_, _) => Ui.Launch("https://github.com/elliotttate/cow-worktrees#readme");
        links.Children.Add(logs);
        links.Children.Add(web);
        panel.Children.Add(links);

        var dialog = new ContentDialog { Title = "App settings and About", Content = panel, PrimaryButtonText = "Save", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Primary };
        checkNow.Click += async (_, _) =>
        {
            checkNow.IsEnabled = false;
            checkResult.Text = "Checking…";
            var (update, whyNot) = await new UpdateService().CheckAsync(null);
            checkNow.IsEnabled = true;
            if (update == null) checkResult.Text = char.ToUpperInvariant(whyNot[0]) + whyNot[1..] + ".";
            else
            {
                checkResult.Text = $"Version {update.Version} is available. Close this to see it at the top of the window.";
                ShowUpdateBanner(update);
            }
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        VM.Settings.CheckForUpdates = updates.IsOn;
        string newEditor = editor.Text.Trim().Trim('"');
        bool editorChanged = !newEditor.Equals(VM.Settings.EditorPath, StringComparison.OrdinalIgnoreCase);
        VM.Settings.EditorPath = newEditor;
        VM.Settings.Save();
        if (editorChanged) VM.ReloadEditor();
    }

    // ---- updates --------------------------------------------------------------------------------------------------

    /// <summary>Once a day at most, and never in an unattended run: is there a newer release?</summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            string current = UpdateService.CurrentVersion;
            string previous = VM.Settings.LastRunVersion;
            if (previous != current)
            {
                VM.Settings.LastRunVersion = current;
                VM.Settings.Save();
                if (previous.Length > 0 && UpdateService.IsNewer(current, previous)) ShowUpdatedNote(current);
            }
            if (_options.Unattended || _options.Hidden || !VM.Settings.CheckForUpdates || UpdateService.DisabledByEnvironment) return;
            if (VM.Settings.LastUpdateCheck is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(24)) return;
            VM.Settings.LastUpdateCheck = DateTime.UtcNow;
            VM.Settings.Save();
            _updates ??= new UpdateService();
            var (update, _) = await _updates.CheckAsync(VM.Settings.SkippedVersion);
            if (update != null) ShowUpdateBanner(update);
        }
        catch (Exception ex)
        {
            AppLog.Write($"update check: {ex.Message}");
        }
    }

    private void ShowUpdatedNote(string version)
    {
        _update = null;
        UpdateInfoBar.Severity = InfoBarSeverity.Success;
        UpdateInfoBar.Title = $"Updated to version {version}";
        UpdateInfoBar.Message = "What's new is on the release page.";
        UpdateNowButton.Visibility = Visibility.Collapsed;
        UpdateInfoBar.IsOpen = true;
    }

    private void ShowUpdateBanner(UpdateInfo update)
    {
        _update = update;
        bool selfUpdate = ToolFolder.IsInstalled(VM.ToolDir) && update.HasInstaller;
        UpdateInfoBar.Severity = InfoBarSeverity.Informational;
        UpdateInfoBar.Title = $"Version {update.Version} is available";
        UpdateInfoBar.Message = selfUpdate
            ? $"You have {UpdateService.CurrentVersion}. Update downloads the installer, checks it, and restarts the app."
            : $"You have {UpdateService.CurrentVersion}. This copy does not update itself: get the new version from the release page.";
        UpdateNowButton.Content = selfUpdate ? "Update" : "Get the update";
        UpdateNowButton.Visibility = Visibility.Visible;
        UpdateNowButton.IsEnabled = true;
        UpdateInfoBar.IsOpen = true;
    }

    private void UpdateInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        // "Later": the banner comes back with the next daily check
    }

    private void UpdateNotes_Click(object sender, RoutedEventArgs e) => Ui.Launch(_update?.PageUrl ?? UpdateService.ReleasesPage);

    private void UpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_update != null)
        {
            VM.Settings.SkippedVersion = _update.Version;
            VM.Settings.Save();
        }
        UpdateInfoBar.IsOpen = false;
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } update || _updating) return;
        if (!ToolFolder.IsInstalled(VM.ToolDir) || !update.HasInstaller)
        {
            Ui.Launch(update.PageUrl);
            return;
        }
        if (VM.RunningCount > 0 || VM.BaseRefreshLockHeld)
        {
            await ShowDialogAsync(new ContentDialog
            {
                Title = "Something is still running",
                Content = Para("Updating replaces the scripts, so it waits until nothing is running: no job in this app, and no refresh of a base copy. Try again when they have finished."),
                CloseButtonText = "OK",
            });
            return;
        }
        _updating = true;
        UpdateNowButton.IsEnabled = false;
        try
        {
            UpdateInfoBar.Message = "Downloading…";
            _updates ??= new UpdateService();
            var result = await _updates.DownloadAsync(update, p => DispatcherQueue.TryEnqueue(() => UpdateInfoBar.Message = $"Downloading… {p:P0}"));
            if (!result.Verified || result.File == null)
            {
                UpdateInfoBar.Severity = InfoBarSeverity.Warning;
                UpdateInfoBar.Message = result.Message;
                UpdateNowButton.Content = "Get the update";
                UpdateNowButton.IsEnabled = true;
                _update = update with { AssetUrl = null };   // from now on the button opens the release page
                return;
            }
            UpdateInfoBar.Message = "Installing… the app closes and opens again.";
            var psi = new System.Diagnostics.ProcessStartInfo(result.File) { UseShellExecute = false };
            psi.ArgumentList.Add("/VERYSILENT");
            psi.ArgumentList.Add("/SUPPRESSMSGBOXES");
            System.Diagnostics.Process.Start(psi)?.Dispose();
            _closeConfirmed = true;
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Write($"update: {ex}");
            UpdateInfoBar.Severity = InfoBarSeverity.Warning;
            UpdateInfoBar.Message = $"The update could not be started ({ex.Message}). Get it from the release page.";
            UpdateNowButton.IsEnabled = true;
        }
        finally
        {
            _updating = false;
        }
    }

    // ---- unattended: checks, setup, the walkthrough and the update check, for tests and screenshots ---------------

    /// <summary>Runs before the snapshot or dump of an unattended start. Returns false when the run is complete and the app should exit.</summary>
    private async Task PrepareUnattendedAsync()
    {
        var json = new JsonSerializerOptions { WriteIndented = true };

        if (_options.SelfTestChecksPath is { } checksOut)
        {
            var report = await Task.Run(SystemChecks.RunAsync);
            await File.WriteAllTextAsync(checksOut, JsonSerializer.Serialize(new
            {
                version = UpdateService.CurrentVersion,
                toolFolder = VM.ToolDir,
                toolFolderRisk = ToolFolder.RiskyLocation(VM.ToolDir),
                installed = ToolFolder.IsInstalled(VM.ToolDir),
                setupScript = VM.SetupScript,
                canSetUp = report.CanSetUp,
                claudeFound = report.ClaudeFound,
                codexFound = report.CodexFound,
                drives = report.RefsDrives.Select(d => new { d.Root, d.Label, d.FreeGB, d.TotalGB }),
                checks = report.Checks.Select(c => new { c.Id, State = c.State.ToString(), c.Summary, c.Detail }),
            }, json));
        }

        if (_options.RunSetupPath is { } runFile)
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(runFile));
            var r = doc.RootElement;
            string resultPath = Json.String(r, "result") ?? Path.ChangeExtension(runFile, ".result.json");
            OperationViewModel? op = null;
            if (Json.Get(r, "setup") is { ValueKind: JsonValueKind.Object } setup)
                op = VM.StartSetup(SetupOptions.FromJson(setup.GetRawText()));
            else if (Json.Get(r, "uninstall") is { ValueKind: JsonValueKind.Object } un)
            {
                string root = Pool.FullPath(Json.String(un, "root") ?? "");
                var pool = VM.Pools.FirstOrDefault(p => p.Root.Equals(root, StringComparison.OrdinalIgnoreCase)) ?? new Pool("", root);
                op = VM.StartUninstall(pool, Json.Get(un, "removePool") is { ValueKind: JsonValueKind.True });
            }
            else if (Json.Get(r, "migrate") is { ValueKind: JsonValueKind.Object } mig && VM.ScriptAvailable)
                op = VM.StartMigrate(Pool.FullPath(Json.String(mig, "from") ?? ""), Json.String(mig, "name") ?? "", Json.Get(mig, "retire") is { ValueKind: JsonValueKind.True });
            else if (Json.Get(r, "create") is { ValueKind: JsonValueKind.Object } create && VM.ScriptAvailable)
            {
                // with stopAfterMs: press Stop that long after the start, to check what a stopped create leaves behind
                op = VM.StartCreate(Json.String(create, "name") ?? "", Json.String(create, "branch"), Json.String(create, "from"));
                if (Json.Get(create, "stopAfterMs") is { ValueKind: JsonValueKind.Number } ms)
                {
                    await Task.Delay(ms.GetInt32());
                    op.RequestCancel();
                }
            }
            if (op != null && Json.Get(r, "stop") is { ValueKind: JsonValueKind.True })
            {
                // press Stop the moment the run allows it (setup: while the base copy is being made)
                while (op.IsRunning && !op.CanCancel) await Task.Delay(40);
                op.RequestCancel();
            }
            if (op != null)
            {
                await op.Settled;
                while (VM.RunningCount > 0) await Task.Delay(300);   // a move is followed by the retiring of the old folder
                await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new
                {
                    operations = VM.Operations.Reverse().Select(o => new { o.Title, Kind = o.Kind.ToString(), o.ExitCode, o.Succeeded, o.Cancelled, last = o.Lines.LastOrDefault()?.Text }),
                    cancelled = op.Cancelled,
                    title = op.Title,
                    commandLine = op.CommandLine,
                    exitCode = op.ExitCode,
                    succeeded = op.Succeeded,
                    steps = op.Steps.Select(s => new { s.Key, s.Label, State = s.State.ToString() }),
                    friendlyTitle = op.Friendly?.Title,
                    friendlyText = op.Friendly?.Explanation,
                    friendlyKnown = op.Friendly?.Known,
                    raw = op.Friendly?.Raw,
                    lines = op.Lines.Select(l => l.Text),
                    pools = VM.Pools.Select(p => new { p.Name, p.Root, p.Repo, p.IsUsable }),
                    selectedPool = VM.Pool?.Name,
                    integrations = VM.Integrations == null ? null : new
                    {
                        claude = VM.Integrations.Claude.State.ToString(),
                        codex = VM.Integrations.Codex.State.ToString(),
                        schedule = VM.Integrations.Schedule.State.ToString(),
                        scheduleAt = VM.Integrations.ScheduleAt,
                        drift = VM.Integrations.Drift,
                    },
                }, json));
            }
        }

        if (_options.UpdateResultPath is { } updateOut)
        {
            var service = new UpdateService(_options.UpdateFeed);
            object result;
            if (!service.IsTestFeed) result = new { error = "--update-result needs --update-feed on http://127.0.0.1 or http://localhost" };
            else
            {
                var (update, why) = await service.CheckAsync(VM.Settings.SkippedVersion);
                UpdateDownload? download = update is { HasInstaller: true } ? await service.DownloadAsync(update) : null;
                // On the test feed the downloaded file is never started: this only says what would have happened.
                result = new
                {
                    current = UpdateService.CurrentVersion,
                    offered = update?.Version,
                    why,
                    hasInstaller = update?.HasInstaller,
                    verified = download?.Verified,
                    message = download?.Message,
                    wouldStart = download is { Verified: true } ? download.File : null,
                    fileExists = download?.File != null && File.Exists(download.File),
                    leftover = update?.AssetName != null && File.Exists(Path.Combine(UpdateService.UpdatesFolder, update.AssetName)),
                };
            }
            await File.WriteAllTextAsync(updateOut, JsonSerializer.Serialize(result, json));
        }

        if (_options.Wizard is { } step)
        {
            var vm = OpenWizard();
            if (_options.WizardStatePath is { } statePath && File.Exists(statePath))
            {
                string state = await File.ReadAllTextAsync(statePath);
                await vm.ApplyStateAsync(state);
                using var doc = JsonDocument.Parse(state);
                vm.GoTo(ParseStep(step));
                if (Json.Get(doc.RootElement, "autoStart") is { ValueKind: JsonValueKind.True } && vm.Step == WizardStep.Review)
                {
                    vm.StartSetup();
                    if (vm.Operation != null) await vm.Operation.Settled;
                }
            }
            else
            {
                if (ParseStep(step) != WizardStep.Welcome) await vm.RunChecksAsync();
                vm.GoTo(ParseStep(step));
            }
            await Task.Delay(900);   // the checks, the folder look-up and layout
        }
        else if (_options.OpenSettings)
        {
            OpenRepositorySettings();
            await Task.Delay(900);
        }
    }

    private static WizardStep ParseStep(string s) =>
        Enum.TryParse<WizardStep>(s, true, out var step) ? step : int.TryParse(s, out var n) ? (WizardStep)Math.Clamp(n, 0, 7) : WizardStep.Welcome;
}
