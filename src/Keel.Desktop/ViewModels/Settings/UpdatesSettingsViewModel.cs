using CommunityToolkit.Mvvm.Input;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Settings;

/// <summary>Settings → Updates (PRD 10): the opt-in update check, off by default, and the installed version.</summary>
public sealed partial class UpdatesSettingsViewModel(UpdateService updates) : ViewModelBase
{
    /// <summary>The update service (status text, availability).</summary>
    public UpdateService Updates => updates;

    /// <summary>"Version 1.0.0-rc.1".</summary>
    public string VersionText => LedgerText.Format(Strings.About_Version, KeelInfo.Version);

    /// <summary>Check for updates on start.</summary>
    public bool CheckForUpdates
    {
        get => updates.IsEnabled;
        set
        {
            if (value != updates.IsEnabled)
            {
                updates.SetEnabled(value);
                OnPropertyChanged();
                CheckNowCommand.NotifyCanExecuteChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync() => updates.CheckAsync(announce: false);

    private bool CanCheck() => updates.IsEnabled;

    [RelayCommand]
    private Task InstallAsync() => updates.ApplyAsync();
}
