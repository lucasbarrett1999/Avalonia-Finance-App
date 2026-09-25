using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.FirstRun;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop.ViewModels;

/// <summary>
/// M8 parts of the shell: the first-run setup layer (PRD 9.10), the command palette (PRD 9.1), the menus
/// (PRD 8) and the daily maintenance jobs.
/// </summary>
public sealed partial class ShellViewModel
{
    private IServiceProvider? _services;

    /// <summary>Raised when a command asks for the global search box to take focus.</summary>
    public event EventHandler? SearchFocusRequested;

    /// <summary>The first-run setup shown over the shell, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstRunVisible))]
    public partial FirstRunViewModel? FirstRun { get; private set; }

    /// <summary>Whether the first-run setup covers the window.</summary>
    public bool IsFirstRunVisible => FirstRun is not null;

    /// <summary>Palette commands and menus (null in view-model-only tests without DI).</summary>
    public AppCommands? Commands { get; private set; }

    /// <summary>Daily integrity check and automatic backup.</summary>
    public MaintenanceJobs? Maintenance { get; private set; }

    /// <summary>The palette currently open (tests).</summary>
    public CommandPaletteViewModel? Palette { get; private set; }

    /// <summary>Closes the first-run layer (the setup finished or was skipped).</summary>
    public void EndFirstRun() => FirstRun = null;

    /// <summary>Opens Settings, scrolled to <paramref name="section"/> ("General", "Connections", "Keyboard"…).</summary>
    public void NavigateToSettings(string? section) => _navigation.NavigateTo<SettingsViewModel>(section);

    /// <summary>Asks the view to focus the global search box.</summary>
    public void RequestSearchFocus() => SearchFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Starts a new transaction in the current register, or in All accounts.</summary>
    public async Task AddTransactionAsync()
    {
        if (CurrentPage is not AccountsViewModel)
        {
            _navigation.NavigateTo<AccountsViewModel>();
        }

        if (CurrentPage is AccountsViewModel register)
        {
            await register.NewTransactionAsync();
        }
    }

    /// <summary>Imports a bank file into the open register's account, or asks which account first.</summary>
    public async Task ImportIntoCurrentOrChosenAccountAsync()
    {
        if (CurrentPage is AccountsViewModel { AccountId: { } current })
        {
            await _import.ImportAsync(current);
            return;
        }

        var accounts = AccountGroups.SelectMany(g => g.Accounts).ToList();
        if (accounts.Count == 0)
        {
            Status.Show(Strings.Palette_ImportNeedsAccount, isError: true);
            return;
        }

        var picker = new PickerDialogViewModel(Strings.Palette_ChooseImportAccount, Strings.Palette_ChooseImportPrompt, accounts.Select(a => new PickerItem(a.Id, a.Name)).ToList());
        if (await Dialogs.ShowAsync(picker) && picker.Selected is { } chosen)
        {
            await ImportFileAsync(chosen.Id);
        }
    }

    /// <summary>Opens the command palette and runs the chosen command.</summary>
    [RelayCommand]
    public async Task OpenCommandPaletteAsync()
    {
        if (Commands is null || Palette is not null || FirstRun is not null)
        {
            return;
        }

        Palette = new CommandPaletteViewModel(Commands.Build(this));
        try
        {
            if (await Dialogs.ShowAsync(Palette) && Palette.Chosen is { } command)
            {
                command.Execute();
            }
        }
        finally
        {
            Palette = null;
        }
    }

    private void InitializeM8(AppSession session, IServiceProvider? services)
    {
        _services = services;
        if (services is null)
        {
            return;
        }

        Commands = services.GetService<AppCommands>();
        if (session.FirstRun is { } step && services.GetService<FirstRunViewModel>() is { } firstRun)
        {
            firstRun.Begin(step, this);
            FirstRun = firstRun;
        }

        Maintenance = services.GetService<MaintenanceJobs>();
        Maintenance?.Start();
        InitializeM9e(session);
    }
}
