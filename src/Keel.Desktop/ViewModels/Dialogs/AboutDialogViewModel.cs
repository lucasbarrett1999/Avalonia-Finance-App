using CommunityToolkit.Mvvm.Input;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Sync;

namespace Keel.Desktop.ViewModels.Dialogs;

/// <summary>About Keel: version, what the app is, license, and links (PRD 8 menus).</summary>
public sealed partial class AboutDialogViewModel(IBrowserLauncher browser) : DialogViewModel
{
    /// <inheritdoc />
    public override string Title => Strings.About_Title;

    /// <summary>"Version 1.0.0-rc.1".</summary>
    public string VersionText => LedgerText.Format(Strings.About_Version, KeelInfo.Version);

    /// <summary>Runtime and OS line.</summary>
    public string RuntimeText => $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";

    /// <inheritdoc />
    protected override Task<bool> ConfirmCoreAsync() => Task.FromResult(true);

    [RelayCommand]
    private Task OpenRepositoryAsync() => browser.OpenAsync(KeelInfo.Repository);

    [RelayCommand]
    private Task OpenGuideAsync() => browser.OpenAsync(KeelInfo.UserGuide);
}
