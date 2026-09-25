using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Accounts;
using Keel.Application.Categories;
using Keel.Application.Files;
using Keel.Application.Ledger;
using Keel.Application.Settings;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Budget;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.FirstRun;

/// <summary>
/// The first-run setup (PRD 9.10, normative): 1. Welcome, create a new budget file or open an existing
/// one; 2. choose a starter template (or empty); 3. add the first account with its balance, or read how
/// bank connections work; 4. land on Budget with Ready to Assign showing the opening balance, and the
/// "Get started" checklist on Home. Creating or opening a file starts a new session (ADR 0080), which
/// continues at step 2.
/// </summary>
public sealed partial class FirstRunViewModel : ViewModelBase
{
    private readonly BudgetSessions _sessions;
    private readonly IFileDialogs _files;
    private readonly ICategoryService _categories;
    private readonly IAccountService _accounts;
    private readonly IAppSettingsStore _settings;
    private readonly IDataDirectory _data;
    private readonly TimeProvider _time;
    private readonly Sync.IBrowserLauncher? _browser;
    private ShellViewModel? _shell;

    /// <summary>Creates the setup.</summary>
    public FirstRunViewModel(BudgetSessions sessions, IFileDialogs files, ICategoryService categories, IAccountService accounts, IAppSettingsStore settings, IDataDirectory data, TimeProvider time, Sync.IBrowserLauncher? browser = null)
    {
        _browser = browser;
        _sessions = sessions;
        _files = files;
        _categories = categories;
        _accounts = accounts;
        _settings = settings;
        _data = data;
        _time = time;
        FileName = Strings.DataFile_NewFileName;
        FileFolder = data.BudgetsDirectory;
        Templates = [.. BudgetTemplate.All.Select(t => new TemplateChoice(t, t.Name, t.Preview)), new TemplateChoice(null, Strings.FirstRun_EmptyTemplate, Strings.FirstRun_EmptyTemplateHint)];
        SelectedTemplate = Templates[0];
        AccountTypes = [AccountType.Checking, AccountType.Savings, AccountType.Cash, AccountType.CreditCard];
        AccountTypeNames = AccountTypes.Select(LedgerText.AccountType).ToList();
        AccountName = Strings.FirstRun_DefaultAccountName;
        Currency = DefaultCurrency();
    }

    /// <summary>The step on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsTemplate), nameof(IsAccount), nameof(StepText), nameof(StepNumber))]
    public partial FirstRunStep Step { get; private set; }

    /// <summary>Step 1.</summary>
    public bool IsWelcome => Step == FirstRunStep.Welcome;

    /// <summary>Step 2.</summary>
    public bool IsTemplate => Step == FirstRunStep.Template;

    /// <summary>Step 3.</summary>
    public bool IsAccount => Step == FirstRunStep.Account;

    /// <summary>1-based step shown as "Step 2 of 3".</summary>
    public int StepNumber => (int)Step + 1;

    /// <summary>"Step 2 of 3".</summary>
    public string StepText => LedgerText.Format(Strings.FirstRun_StepOf, StepNumber, 3);

    /// <summary>A step is working (creating the file, applying the template…).</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>What went wrong, shown in the step.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    /// <summary>Whether <see cref="Error"/> is set.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Name of the new budget file (without extension).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewFilePath))]
    public partial string FileName { get; set; }

    /// <summary>Folder of the new budget file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewFilePath))]
    public partial string FileFolder { get; set; }

    /// <summary>Where the new file will be created.</summary>
    public string NewFilePath => Path.Combine(FileFolder, (string.IsNullOrWhiteSpace(FileName) ? Strings.DataFile_NewFileName : FileName.Trim()) + IBudgetFileService.Extension);

    /// <summary>Starter templates plus "Start empty".</summary>
    public IReadOnlyList<TemplateChoice> Templates { get; }

    /// <summary>The chosen template.</summary>
    [ObservableProperty]
    public partial TemplateChoice SelectedTemplate { get; set; }

    /// <summary>Account types offered for the first account.</summary>
    public IReadOnlyList<AccountType> AccountTypes { get; }

    /// <summary>Their names.</summary>
    public IReadOnlyList<string> AccountTypeNames { get; }

    /// <summary>Index into <see cref="AccountTypes"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BalanceLabel))]
    public partial int AccountTypeIndex { get; set; }

    /// <summary>Account name.</summary>
    [ObservableProperty]
    public partial string AccountName { get; set; }

    /// <summary>Current balance (minor units; for a credit card, the amount owed).</summary>
    [ObservableProperty]
    public partial long Balance { get; set; }

    /// <summary>ISO currency of the budget (from the OS region).</summary>
    [ObservableProperty]
    public partial string Currency { get; set; }

    /// <summary>"Current balance" or "Amount owed".</summary>
    public string BalanceLabel => AccountTypeInfo.IsLiability(AccountTypes[AccountTypeIndex]) ? Strings.AccountEditor_AmountOwed : Strings.AccountEditor_Balance;

    /// <summary>The bank-connection explanation is expanded.</summary>
    [ObservableProperty]
    public partial bool ShowBankInfo { get; set; }

    /// <summary>The account created in step 3 (tests).</summary>
    public AccountDto? CreatedAccount { get; private set; }

    /// <summary>The latest step action (tests await it).</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Shows the setup at <paramref name="step"/> inside <paramref name="shell"/>.</summary>
    public void Begin(FirstRunStep step, ShellViewModel shell)
    {
        _shell = shell;
        Step = step;
    }

    /// <summary>Step 1: create the budget file and continue in it.</summary>
    [RelayCommand]
    public Task CreateAsync() => Running = RunAsync(async () =>
    {
        var path = NewFilePath;
        if (File.Exists(path))
        {
            Error = LedgerText.Format(Strings.DataFile_Exists, Path.GetFileName(path));
            return;
        }

        await _sessions.OpenAsync(path, new BudgetStartupOptions(ResumeFirstRun: true, Message: Strings.DataFile_CreatedStatus));
    });

    /// <summary>Step 1: choose another folder or name for the new file.</summary>
    [RelayCommand]
    public async Task ChooseLocationAsync()
    {
        var path = await _files.SaveBudgetFileAsync(Strings.FileDialog_NewTitle, NewFilePath, FileFolder);
        if (path is not null)
        {
            FileFolder = Path.GetDirectoryName(path) ?? FileFolder;
            FileName = Path.GetFileNameWithoutExtension(path);
        }
    }

    /// <summary>Step 1: open a budget file the user already has; the setup ends.</summary>
    [RelayCommand]
    public Task OpenExistingAsync() => Running = RunAsync(async () =>
    {
        var path = await _files.OpenBudgetFileAsync(_data.BudgetsDirectory);
        if (path is null)
        {
            return;
        }

        // An encrypted file opens locked and the new shell asks for its passphrase (this layer would hide a prompt).
        await _sessions.OpenAsync(path, new BudgetStartupOptions(AllowLocked: true));
        _settings.Update(s => s with { FirstRunCompleted = true });
    });

    /// <summary>Step 2: apply the chosen template and go to step 3.</summary>
    [RelayCommand]
    public Task ApplyTemplateAsync() => Running = RunAsync(async () =>
    {
        if (SelectedTemplate.Template is { } template)
        {
            await Task.Run(() => _categories.ApplyTemplateAsync(template.Groups, CancellationToken.None));
        }

        Step = FirstRunStep.Account;
    });

    /// <summary>Step 3: create the first account and land on Budget.</summary>
    [RelayCommand]
    public Task AddAccountAsync() => Running = RunAsync(async () =>
    {
        var type = AccountTypes[AccountTypeIndex];
        var amount = AccountTypeInfo.IsLiability(type) ? -Math.Abs(Balance) : Balance;
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        try
        {
            CreatedAccount = await Task.Run(() => _accounts.CreateAccountAsync(
                new CreateAccountRequest(AccountName?.Trim() ?? string.Empty, type, (Currency ?? string.Empty).Trim().ToUpperInvariant(), today, amount),
                CancellationToken.None));
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return;
        }

        Finish(toBudget: true);
    });

    /// <summary>Step 3: skip the account (Home's checklist keeps the step open).</summary>
    [RelayCommand]
    public void Skip() => Finish(toBudget: false);

    /// <summary>Step 3: go to Settings → Connections to link a bank instead.</summary>
    [RelayCommand]
    public void ConnectBank()
    {
        Finish(toBudget: false);
        _shell?.NavigateToSettings("Connections");
    }

    /// <summary>Step 3: opens the bank sync guide in the browser.</summary>
    [RelayCommand]
    public Task OpenBankGuideAsync() => _browser?.OpenAsync(KeelInfo.BankSyncGuide) ?? Task.CompletedTask;

    /// <summary>Step 3: shows or hides the explanation of bank connections.</summary>
    [RelayCommand]
    public void ToggleBankInfo() => ShowBankInfo = !ShowBankInfo;

    /// <summary>Goes back one step (steps 2 and 3).</summary>
    [RelayCommand]
    public void Back()
    {
        if (Step == FirstRunStep.Account)
        {
            Step = FirstRunStep.Template;
        }
    }

    private void Finish(bool toBudget)
    {
        _settings.Update(s => s with { FirstRunCompleted = true });
        if (_shell is { } shell)
        {
            shell.EndFirstRun();
            if (toBudget)
            {
                shell.NavigateTo<BudgetViewModel>();
            }
            else
            {
                shell.NavigateTo<HomeViewModel>();
            }
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Data.Common.DbException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string DefaultCurrency()
    {
        try
        {
            return CurrencyFor(RegionInfo.CurrentRegion);
        }
        catch (ArgumentException)
        {
            return Keel.Domain.Currency.Default;
        }
    }

    /// <summary>
    /// The budget currency suggested for <paramref name="region"/>: its ISO currency, or the default when the
    /// region is the invariant one (a "C" or POSIX locale reports XDR, special drawing rights) or unknown.
    /// </summary>
    public static string CurrencyFor(RegionInfo? region)
    {
        var code = region?.ISOCurrencySymbol;
        return region is null || region.Name is "IV" or "" || code is null or "XDR" or "XXX" || !Keel.Domain.Currency.IsValidCode(code)
            ? Keel.Domain.Currency.Default
            : code;
    }
}

/// <summary>A starter template choice (null template = start empty).</summary>
/// <param name="Template">The template, or null for no categories.</param>
/// <param name="Name">Name.</param>
/// <param name="Description">Groups and categories it creates.</param>
public sealed record TemplateChoice(BudgetTemplate? Template, string Name, string Description);
