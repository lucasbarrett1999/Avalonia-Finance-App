using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Backup;
using Keel.Application.Files;
using Keel.Application.Settings;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Rules;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop.ViewModels.Settings;

/// <summary>
/// Settings → General (F-SET-1, PRD 9.9): where the budget file is, move it, create or open another,
/// back up now, automatic daily backups with keep-N, restore with confirmation, the integrity check and
/// the diagnostic bundle. Opening, creating, moving and restoring start a new session (ADR 0080).
/// </summary>
public sealed partial class DataFileSettingsViewModel : ViewModelBase
{
    private readonly AppSession _session;
    private readonly IDataDirectory _data;
    private readonly IBackupService _backups;
    private readonly IDataFileMaintenance _maintenance;
    private readonly BudgetSessions _sessions;
    private readonly DialogService _dialogs;
    private readonly StatusService _status;
    private readonly IFileDialogs _files;
    private readonly IAppSettingsStore _settings;

    /// <summary>Creates the section.</summary>
    public DataFileSettingsViewModel(
        AppSession session,
        IDataDirectory data,
        IBackupService backups,
        IDataFileMaintenance maintenance,
        BudgetSessions sessions,
        DialogService dialogs,
        StatusService status,
        IFileDialogs files,
        IAppSettingsStore settings,
        Portability.PortabilitySettingsViewModel? portability = null)
    {
        Portability = portability;
        _session = session;
        _data = data;
        _backups = backups;
        _maintenance = maintenance;
        _sessions = sessions;
        _dialogs = dialogs;
        _status = status;
        _files = files;
        _settings = settings;
    }

    /// <summary>Export, bundle import and YNAB/Monarch import (M9, F-REP-6).</summary>
    public Portability.PortabilitySettingsViewModel? Portability { get; }

    /// <summary>Full path of the open budget file.</summary>
    public string BudgetFilePath => _session.BudgetFile?.Path ?? Strings.Shell_NoFile;

    /// <summary>Whether a budget file is open (file actions need one).</summary>
    public bool HasFile => _session.BudgetFile is not null;

    /// <summary>Data directory root.</summary>
    public string DataFolderPath => _data.Root;

    /// <summary>Log folder.</summary>
    public string LogsFolderPath => _data.LogsDirectory;

    /// <summary>Backups folder.</summary>
    public string BackupsFolderPath => _data.BackupsDirectory;

    /// <summary>Backups of the open file, newest first.</summary>
    public ObservableCollection<BackupRowViewModel> Backups { get; } = [];

    /// <summary>Whether any backup exists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoBackups))]
    public partial bool HasBackups { get; private set; }

    /// <summary>The backups list is loading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoBackups))]
    public partial bool IsLoadingBackups { get; private set; }

    /// <summary>Empty state of the backups list.</summary>
    public bool ShowNoBackups => !HasBackups && !IsLoadingBackups && BackupsError is null;

    /// <summary>Why the backups list could not be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoBackups))]
    public partial string? BackupsError { get; private set; }

    /// <summary>A file operation is running.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Result of the last integrity check in this session.</summary>
    [ObservableProperty]
    public partial string? IntegrityText { get; private set; }

    /// <summary>The last integrity check failed.</summary>
    [ObservableProperty]
    public partial bool IntegrityFailed { get; private set; }

    /// <summary>Daily automatic backups.</summary>
    public bool AutoBackupEnabled
    {
        get => _settings.Current.AutoBackupEnabled;
        set
        {
            if (value != AutoBackupEnabled)
            {
                _settings.Update(s => s with { AutoBackupEnabled = value });
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Automatic backups kept (1–365).</summary>
    public decimal AutoBackupKeep
    {
        get => _settings.Current.AutoBackupKeep;
        set
        {
            var keep = (int)Math.Clamp(value, 1, 365);
            if (keep != _settings.Current.AutoBackupKeep)
            {
                _settings.Update(s => s with { AutoBackupKeep = keep });
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The latest backups refresh (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Reloads the backups list.</summary>
    public Task LoadAsync() => Loading = LoadCoreAsync();

    /// <summary>Creates a new budget file and continues with the starter template and first account.</summary>
    [RelayCommand]
    public async Task NewFileAsync()
    {
        var path = await _files.SaveBudgetFileAsync(Strings.FileDialog_NewTitle, Strings.DataFile_NewFileName + IBudgetFileService.Extension, _data.BudgetsDirectory);
        if (path is null)
        {
            return;
        }

        if (File.Exists(path))
        {
            _status.Show(LedgerText.Format(Strings.DataFile_Exists, Path.GetFileName(path)), isError: true);
            return;
        }

        await SwitchAsync(path, new BudgetStartupOptions(ResumeFirstRun: true, Message: Strings.DataFile_CreatedStatus));
    }

    /// <summary>Opens another budget file.</summary>
    [RelayCommand]
    public async Task OpenFileAsync()
    {
        var start = _session.BudgetFile is { } file ? Path.GetDirectoryName(file.Path) : _data.BudgetsDirectory;
        var path = await _files.OpenBudgetFileAsync(start);
        if (path is not null)
        {
            await SwitchAsync(path, null, promptForPassphrase: true);
        }
    }

    /// <summary>Moves the budget file and its attachments folder to a new place (e.g. a synced folder).</summary>
    [RelayCommand]
    public async Task ChangeLocationAsync()
    {
        if (_session.BudgetFile is not { } current)
        {
            return;
        }

        var path = await _files.SaveBudgetFileAsync(Strings.FileDialog_MoveTitle, current.FileName, null);
        if (path is null || BudgetSessions.PathsEqual(path, current.Path))
        {
            return;
        }

        var confirm = new ConfirmDialogViewModel(
            Strings.DataFile_MoveTitle,
            LedgerText.Format(Strings.DataFile_MoveMessage, current.FileName, Path.GetDirectoryName(path)),
            Strings.DataFile_MoveConfirm);
        if (!await _dialogs.ShowAsync(confirm))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _maintenance.CopyToAsync(path, CancellationToken.None);
            await _sessions.OpenAsync(path, new BudgetStartupOptions(Message: LedgerText.Format(Strings.DataFile_MovedStatus, Path.GetDirectoryName(path))));
            try
            {
                await _sessions.Current!.Services.GetRequiredService<IDataFileMaintenance>().DeleteBudgetFileAsync(current.Path, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _sessions.Current!.Services.GetRequiredService<StatusService>().Show(LedgerText.Format(Strings.DataFile_OldCopyKept, current.Path, ex.Message), isError: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or BackupVerificationException or System.Data.Common.DbException)
        {
            _status.Show(LedgerText.Format(Strings.DataFile_MoveFailed, ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Backs up now (verified zip).</summary>
    [RelayCommand]
    public async Task BackupNowAsync()
    {
        IsBusy = true;
        try
        {
            var backup = await Task.Run(() => _backups.BackupNowAsync(CancellationToken.None));
            _status.Show(LedgerText.Format(Strings.DataFile_BackedUp, backup.FileName));
            await LoadAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or BackupVerificationException)
        {
            _status.Show(LedgerText.Format(Strings.DataFile_BackupFailed, ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Restores a listed backup after confirmation.</summary>
    [RelayCommand]
    public Task RestoreAsync(BackupRowViewModel? row) => row is null ? Task.CompletedTask : RestoreCoreAsync(row.Info.Path, row.DateText);

    /// <summary>Restores a backup zip chosen with the file picker.</summary>
    [RelayCommand]
    public async Task RestoreFromFileAsync()
    {
        var path = await _files.OpenBackupAsync(_data.BackupsDirectory);
        if (path is not null)
        {
            await RestoreCoreAsync(path, Path.GetFileName(path));
        }
    }

    /// <summary>Runs the integrity check now.</summary>
    [RelayCommand]
    public async Task CheckIntegrityAsync()
    {
        IsBusy = true;
        try
        {
            var result = await Task.Run(() => _maintenance.CheckIntegrityAsync(CancellationToken.None));
            IntegrityFailed = !result.IsOk;
            IntegrityText = result.IsOk
                ? LedgerText.Format(Strings.DataFile_IntegrityOk, DateTime.Now.ToString("t", CultureInfo.CurrentCulture))
                : LedgerText.Format(Strings.DataFile_IntegrityFailed, string.Join("; ", result.Problems.Take(3)));
            _status.Show(IntegrityText, isError: !result.IsOk);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Data.Common.DbException)
        {
            IntegrityFailed = true;
            IntegrityText = LedgerText.Format(Strings.DataFile_IntegrityFailed, ex.Message);
            _status.Show(IntegrityText, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Writes the diagnostic bundle (logs and a schema-only summary) and copies its path.</summary>
    [RelayCommand]
    public async Task CopyDiagnosticBundleAsync()
    {
        IsBusy = true;
        try
        {
            var path = await Task.Run(() => _maintenance.CreateDiagnosticBundleAsync(Path.Combine(_data.Root, "diagnostics"), KeelInfo.Version, CancellationToken.None));
            LastDiagnosticBundle = path;
            var clipboard = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(path);
            }

            _status.Show(LedgerText.Format(Strings.DataFile_DiagnosticsSaved, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Data.Common.DbException)
        {
            _status.Show(LedgerText.Format(Strings.DataFile_DiagnosticsFailed, ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Path of the last diagnostic bundle (tests).</summary>
    public string? LastDiagnosticBundle { get; private set; }

    private async Task RestoreCoreAsync(string zip, string label)
    {
        if (_session.BudgetFile is not { } current)
        {
            return;
        }

        var confirm = new ConfirmDialogViewModel(Strings.DataFile_RestoreTitle, LedgerText.Format(Strings.DataFile_RestoreMessage, label, current.FileName), Strings.DataFile_RestoreConfirm);
        if (!await _dialogs.ShowAsync(confirm))
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (!await RestoreBackupAsync(zip))
            {
                return;
            }

            await _sessions.OpenAsync(current.Path, new BudgetStartupOptions(Message: LedgerText.Format(Strings.DataFile_RestoredStatus, label)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or BackupVerificationException or System.Data.Common.DbException)
        {
            _status.Show(LedgerText.Format(Strings.DataFile_RestoreFailed, ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // A backup made under another passphrase (F-SET-4) asks for that passphrase; false when the user cancels.
    private async Task<bool> RestoreBackupAsync(string zip)
    {
        try
        {
            await Task.Run(() => _backups.RestoreAsync(zip, CancellationToken.None));
            return true;
        }
        catch (Keel.Application.Files.BudgetFileLockedException)
        {
            var restored = false;
            var prompt = new ViewModels.Dialogs.UnlockFileViewModel(zip, async (passphrase, _) =>
            {
                await Task.Run(() => _backups.RestoreAsync(zip, passphrase, CancellationToken.None));
                restored = true;
            })
            {
                CanRemember = false,
            };
            await _dialogs.ShowAsync(prompt);
            return restored;
        }
    }

    private async Task SwitchAsync(string path, BudgetStartupOptions? options, bool promptForPassphrase = false)
    {
        IsBusy = true;
        try
        {
            if (promptForPassphrase)
            {
                // An encrypted file asks for its passphrase here, in this shell (F-SET-4).
                await _sessions.OpenWithPromptAsync(path, options);
            }
            else
            {
                await _sessions.OpenAsync(path, options);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _status.Show(LedgerText.Format(Strings.Shell_StatusOpenRequestedFailed, Path.GetFileName(path), ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadCoreAsync()
    {
        if (_session.BudgetFile is null)
        {
            return;
        }

        IsLoadingBackups = true;
        BackupsError = null;
        try
        {
            var list = await Task.Run(() => _backups.ListBackupsAsync(CancellationToken.None));
            Backups.Clear();
            foreach (var backup in list)
            {
                Backups.Add(new BackupRowViewModel(backup, RestoreCommand));
            }

            HasBackups = Backups.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BackupsError = LedgerText.Format(Strings.DataFile_BackupsError, ex.Message);
        }
        finally
        {
            IsLoadingBackups = false;
        }
    }
}

/// <summary>A backup in Settings → General.</summary>
/// <param name="Info">The backup.</param>
/// <param name="Restore">Restores it (after confirmation).</param>
public sealed record BackupRowViewModel(BackupInfo Info, IAsyncRelayCommand<BackupRowViewModel?> Restore)
{
    /// <summary>"Sep 24, 2026 10:15".</summary>
    public string DateText => Info.CreatedAt.ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Manual, automatic, before migration, before restore.</summary>
    public string KindText => Strings.ResourceManager.GetString("BackupKind_" + Info.Kind, Strings.Culture) ?? Info.Kind.ToString();

    /// <summary>"1.2 MB".</summary>
    public string SizeText => Info.SizeBytes >= 1024 * 1024
        ? (Info.SizeBytes / 1024d / 1024d).ToString("0.0 'MB'", CultureInfo.CurrentCulture)
        : Math.Max(1, Info.SizeBytes / 1024).ToString("0 'KB'", CultureInfo.CurrentCulture);

    /// <summary>Screen-reader text of the Restore button.</summary>
    public string RestoreName => LedgerText.Format(Strings.DataFile_RestoreName, DateText, KindText);
}
