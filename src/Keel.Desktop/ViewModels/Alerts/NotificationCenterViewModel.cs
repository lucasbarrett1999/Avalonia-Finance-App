using System.Data.Common;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Alerts;
using Keel.Application.Navigation;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Alerts;

/// <summary>
/// The notification center behind the top-bar bell (PRD 9.1, F-REC-3): the unread badge and a panel of
/// alerts, newest first, each with its kind icon, a dismiss button and a link to the related item (Bills)
/// or transaction (register). Alerts never leave the machine. Refreshes on <see cref="AlertsChanged"/>.
/// </summary>
public sealed partial class NotificationCenterViewModel : ViewModelBase, IRecipient<AlertsChanged>
{
    /// <summary>Largest count shown in the badge ("9+" above).</summary>
    public const int BadgeMax = 9;

    private readonly IAlertService _alerts;
    private readonly INavigationService _navigation;
    private readonly StatusService _status;
    private readonly TimeProvider _time;
    private int _version;

    /// <summary>Creates the notification center and loads the badge.</summary>
    public NotificationCenterViewModel(IAlertService alerts, INavigationService navigation, StatusService status, TimeProvider time, IMessenger messenger, AppSession session)
    {
        ArgumentNullException.ThrowIfNull(messenger);
        ArgumentNullException.ThrowIfNull(session);
        _alerts = alerts;
        _navigation = navigation;
        _status = status;
        _time = time;
        messenger.Register(this);
        Loading = session.BudgetFile is null ? Task.CompletedTask : LoadAsync();
    }

    /// <summary>Alerts, newest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    public partial IReadOnlyList<AlertItemViewModel> Items { get; private set; } = [];

    /// <summary>Whether there is anything to show.</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>Unread, undismissed alerts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread), nameof(BadgeText), nameof(BellName))]
    public partial int UnreadCount { get; private set; }

    /// <summary>Whether the badge shows.</summary>
    public bool HasUnread => UnreadCount > 0;

    /// <summary>"3" or "9+".</summary>
    public string BadgeText => UnreadCount > BadgeMax ? BadgeMax.ToString(System.Globalization.CultureInfo.CurrentCulture) + "+" : UnreadCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Accessible name of the bell: "Notifications, 3 unread".</summary>
    public string BellName => UnreadCount == 0 ? Strings.Alerts_Title : LedgerText.Format(Strings.Alerts_BellUnread, UnreadCount);

    /// <summary>Whether the panel is open.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>The current load (tests await it).</summary>
    public Task Loading { get; private set; }

    /// <inheritdoc />
    public void Receive(AlertsChanged message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Dispatcher.UIThread.Post(() =>
        {
            UnreadCount = message.UnreadCount;
            Loading = LoadAsync();
        });
    }

    /// <summary>Opens or closes the panel.</summary>
    [RelayCommand]
    public void Toggle()
    {
        IsOpen = !IsOpen;
        if (IsOpen)
        {
            Loading = LoadAsync();
        }
    }

    /// <summary>Closes the panel.</summary>
    [RelayCommand]
    public void Close() => IsOpen = false;

    /// <summary>Marks every alert read.</summary>
    [RelayCommand]
    public async Task MarkAllReadAsync()
    {
        var unread = Items.Where(i => i.IsUnread).Select(i => i.Id).ToList();
        if (unread.Count > 0)
        {
            await RunAsync(() => _alerts.MarkReadAsync(unread, CancellationToken.None));
        }
    }

    /// <summary>Dismisses one alert.</summary>
    [RelayCommand]
    public Task DismissAsync(AlertItemViewModel? item) =>
        item is null ? Task.CompletedTask : RunAsync(() => _alerts.DismissAsync(item.Id, CancellationToken.None));

    /// <summary>Opens what the alert is about: its recurring item in Bills, else its transaction in the register.</summary>
    [RelayCommand]
    public async Task OpenAsync(AlertItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsUnread)
        {
            await RunAsync(() => _alerts.MarkReadAsync([item.Id], CancellationToken.None));
        }

        IsOpen = false;
        if (item.Alert.RecurringItemId is { } itemId)
        {
            _navigation.NavigateTo<BillsViewModel>(itemId);
        }
        else if (item.Alert.PayeeName is { } payee)
        {
            _navigation.NavigateTo<AccountsViewModel>(new RegisterNavigation(null, "payee:\"" + payee + "\""));
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException)
        {
            _status.Show(ex.Message, isError: true);
        }

        await (Loading = LoadAsync());
    }

    private async Task LoadAsync()
    {
        var version = ++_version;
        try
        {
            var alerts = await _alerts.GetAlertsAsync(includeDismissed: false, CancellationToken.None);
            var unread = await _alerts.GetUnreadCountAsync(CancellationToken.None);
            if (version != _version)
            {
                return;
            }

            var now = _time.GetUtcNow().UtcDateTime;
            Items = alerts.Select(a => new AlertItemViewModel(a, now)).ToList();
            UnreadCount = unread;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException)
        {
            _status.Show(LedgerText.Format(Strings.Alerts_ErrorLoading, ex.Message), isError: true);
        }
    }
}
