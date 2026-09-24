using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Payees;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Register;

namespace Keel.Desktop.ViewModels.Rules;

/// <summary>
/// Settings → Payees (F-TXN-9, partial): search, default category per payee (used by
/// categorization at 95% confidence) and rename, which applies to every transaction of the payee.
/// </summary>
public sealed partial class PayeesViewModel : ObservableObject, IRecipient<LedgerChanged>
{
    /// <summary>Most payees listed at once; searching narrows the list.</summary>
    public const int Limit = 200;

    private readonly IPayeeService _payees;
    private readonly ICategoryService _categories;
    private readonly DialogService _dialogs;
    private readonly StatusService _status;
    private int _version;
    private bool _loaded;

    /// <summary>Creates the view model.</summary>
    public PayeesViewModel(IPayeeService payees, ICategoryService categories, DialogService dialogs, StatusService status, IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(messenger);
        _payees = payees;
        _categories = categories;
        _dialogs = dialogs;
        _status = status;
        messenger.Register(this);
    }

    /// <summary>Payees shown.</summary>
    public ObservableCollection<PayeeRowViewModel> Payees { get; } = [];

    /// <summary>Default-category choices ("No default" first).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CategoryOption> CategoryChoices { get; private set; } = [NoDefault];

    /// <summary>Search text.</summary>
    [ObservableProperty]
    public partial string? SearchText { get; set; }

    /// <summary>Loading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Load error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsEmpty))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Whether loading failed.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Nothing matches.</summary>
    public bool IsEmpty => !IsLoading && !HasError && Payees.Count == 0;

    /// <summary>"No default category".</summary>
    public static CategoryOption NoDefault { get; } = new(null, Strings.Payees_NoDefault, string.Empty);

    /// <summary>The current load (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Loads once.</summary>
    public Task EnsureLoadedAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            Loading = LoadAsync();
        }

        return Loading;
    }

    /// <inheritdoc />
    public void Receive(LedgerChanged message) => Dispatcher.UIThread.Post(() =>
    {
        if (_loaded)
        {
            Loading = LoadAsync();
        }
    });

    [RelayCommand]
    private async Task RenameAsync(PayeeRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var dialog = new RenamePayeeDialogViewModel(_payees, row.Id, row.Name);
        if (await _dialogs.ShowAsync(dialog) && dialog.Result is { } renamed)
        {
            _status.Show(LedgerText.Format(Strings.Status_PayeeRenamed, row.Name, renamed.Name), offerUndo: true);
        }
    }

    partial void OnSearchTextChanged(string? value)
    {
        if (_loaded)
        {
            Loading = LoadAsync();
        }
    }

    private async Task SetDefaultAsync(PayeeRowViewModel row, CategoryOption? category)
    {
        try
        {
            await _payees.SetDefaultCategoryAsync(row.Id, category?.Id, CancellationToken.None);
            _status.Show(category?.Id is null
                ? LedgerText.Format(Strings.Status_PayeeDefaultCleared, row.Name)
                : LedgerText.Format(Strings.Status_PayeeDefaultSet, row.Name, category.FullName), offerUndo: true);
        }
        catch (LedgerValidationException ex)
        {
            _status.Show(LedgerText.Error(ex.Error), isError: true);
        }
    }

    private async Task LoadAsync()
    {
        var version = ++_version;
        IsLoading = Payees.Count == 0;
        try
        {
            ErrorMessage = null;
            var categoriesTask = _categories.GetCategoriesAsync(includeHidden: false, CancellationToken.None);
            var payeesTask = _payees.ListAsync(SearchText, Limit, CancellationToken.None);
            var categories = await categoriesTask;
            var payees = await payeesTask;
            if (version != _version)
            {
                return;
            }

            var choices = new List<CategoryOption> { NoDefault };
            choices.AddRange(categories.Where(c => !c.IsCreditCardPayment && !c.IsSystem).Select(CategoryOption.From));
            CategoryChoices = choices;
            Payees.Clear();
            foreach (var payee in payees)
            {
                Payees.Add(new PayeeRowViewModel(payee, choices, SetDefaultAsync) { Owner = this });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _version)
            {
                ErrorMessage = LedgerText.Format(Strings.Payees_ErrorLoading, ex.Message);
            }
        }
        finally
        {
            if (version == _version)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }
}

/// <summary>One payee row.</summary>
public sealed partial class PayeeRowViewModel : ObservableObject
{
    private readonly Func<PayeeRowViewModel, CategoryOption?, Task> _setDefault;
    private readonly bool _ready;

    /// <summary>Creates the row.</summary>
    public PayeeRowViewModel(PayeeListItem payee, IReadOnlyList<CategoryOption> choices, Func<PayeeRowViewModel, CategoryOption?, Task> setDefault)
    {
        ArgumentNullException.ThrowIfNull(payee);
        ArgumentNullException.ThrowIfNull(choices);
        Payee = payee;
        Choices = choices;
        _setDefault = setDefault;
        DefaultCategory = choices.FirstOrDefault(c => c.Id == payee.DefaultCategoryId) ?? choices[0];
        _ready = true;
    }

    /// <summary>The payee.</summary>
    public PayeeListItem Payee { get; }

    /// <summary>The list (the rename button binds to its command).</summary>
    public PayeesViewModel? Owner { get; init; }

    /// <summary>Id.</summary>
    public Guid Id => Payee.Id;

    /// <summary>Name.</summary>
    public string Name => Payee.Name;

    /// <summary>"12 transactions".</summary>
    public string CountText => LedgerText.Format(Strings.Payees_TransactionCount, Payee.TransactionCount.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>Default-category choices.</summary>
    public IReadOnlyList<CategoryOption> Choices { get; }

    /// <summary>Default category; changing it saves.</summary>
    [ObservableProperty]
    public partial CategoryOption? DefaultCategory { get; set; }

    partial void OnDefaultCategoryChanged(CategoryOption? value)
    {
        if (_ready && value is not null)
        {
            _ = _setDefault(this, value);
        }
    }
}

/// <summary>Rename a payee (applies to all its transactions).</summary>
public sealed partial class RenamePayeeDialogViewModel : DialogViewModel
{
    private readonly IPayeeService _payees;
    private readonly Guid _payeeId;

    /// <summary>Creates the dialog.</summary>
    public RenamePayeeDialogViewModel(IPayeeService payees, Guid payeeId, string currentName)
    {
        _payees = payees;
        _payeeId = payeeId;
        CurrentName = currentName;
        Name = currentName;
    }

    /// <inheritdoc />
    public override string Title => Strings.Payees_RenameTitle;

    /// <summary>The name before renaming.</summary>
    public string CurrentName { get; }

    /// <summary>Explanation.</summary>
    public string Message => LedgerText.Format(Strings.Payees_RenameMessage, CurrentName);

    /// <summary>New name.</summary>
    [ObservableProperty]
    public partial string? Name { get; set; }

    /// <summary>The renamed (or merged) payee.</summary>
    public PayeeDto? Result { get; private set; }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        try
        {
            Result = await _payees.RenameAsync(_payeeId, Name ?? string.Empty, CancellationToken.None);
            return true;
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
    }
}
