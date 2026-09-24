using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Ledger;
using Keel.Application.Payees;
using Keel.Application.Scheduling;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Register;
using Keel.Domain.Scheduling;

namespace Keel.Desktop.ViewModels.Bills;

/// <summary>The recurrence builder's frequencies (F-ACC-6: daily, weekly, every N weeks, monthly, twice monthly, yearly).</summary>
public enum ScheduleFrequency
{
    /// <summary>Every N days.</summary>
    Daily,

    /// <summary>Every N weeks on chosen weekdays.</summary>
    Weekly,

    /// <summary>Every N months on a day or an Nth weekday.</summary>
    Monthly,

    /// <summary>Twice a month on two days.</summary>
    TwiceMonthly,

    /// <summary>Every N years on a date.</summary>
    Yearly,
}

/// <summary>How a schedule ends.</summary>
public enum ScheduleEnd
{
    /// <summary>Never.</summary>
    Never,

    /// <summary>On a date.</summary>
    OnDate,

    /// <summary>After a number of times.</summary>
    AfterCount,
}

/// <summary>A weekday toggle of the weekly builder.</summary>
public sealed partial class WeekdayToggle(DayOfWeek day, Action changed) : ObservableObject
{
    /// <summary>Weekday.</summary>
    public DayOfWeek Day { get; } = day;

    /// <summary>"Mon".</summary>
    public string Label { get; } = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day);

    /// <summary>Full name for screen readers.</summary>
    public string Name { get; } = CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day);

    /// <summary>Selected.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>
/// Create or edit a scheduled transaction (F-ACC-6): account, payee, amount, category or transfer, memo,
/// auto-enter, and a recurrence builder (frequency, interval, day rules, end) that shows the rule in words
/// and its next five dates as the user edits. The rule is built with <see cref="RecurrenceRule"/> and
/// checked by <see cref="IScheduledTransactionService.CheckRule"/>.
/// </summary>
public sealed partial class ScheduledTransactionEditorViewModel : DialogViewModel
{
    /// <summary>Dates previewed under the builder.</summary>
    public const int PreviewCount = 5;

    private readonly IScheduledTransactionService _scheduled;
    private readonly IPayeeService _payees;
    private readonly ScheduledTransactionDto? _existing;
    private readonly Guid? _recurringItemId;
    private bool _loading = true;

    /// <summary>Creates the dialog; <paramref name="existing"/> null creates a new schedule.</summary>
    public ScheduledTransactionEditorViewModel(
        IScheduledTransactionService scheduled,
        IPayeeService payees,
        IReadOnlyList<AccountOption> accounts,
        IReadOnlyList<CategoryOption> categories,
        DateOnly start,
        ScheduledTransactionDto? existing = null,
        ScheduledDraft? draft = null)
    {
        _scheduled = scheduled;
        _payees = payees;
        _existing = existing;
        _recurringItemId = draft?.RecurringItemId;
        Accounts = accounts.Where(a => !a.IsClosed || a.Id == existing?.AccountId).ToList();
        TransferAccounts = [NoTransfer, .. Accounts];
        Categories = [RecurringItemEditorViewModel.NoCategory, .. categories];
        Frequencies = Enum.GetValues<ScheduleFrequency>().Select(f => new Choice<ScheduleFrequency>(f, Label("ScheduleFrequency_" + f))).ToList();
        Ends = Enum.GetValues<ScheduleEnd>().Select(e => new Choice<ScheduleEnd>(e, Label("ScheduleEnd_" + e))).ToList();
        MonthDays = [.. Enumerable.Range(1, 31).Select(d => new Choice<int>(d, OrdinalText(d))), new Choice<int>(-1, Strings.Schedule_LastDay)];
        Ordinals = [.. new[] { 1, 2, 3, 4, -1 }.Select(o => new Choice<int>(o, Label("Schedule_Ordinal" + (o < 0 ? "Last" : o.ToString(CultureInfo.InvariantCulture)))))];
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
        Weekdays = [.. days.Select(d => new Choice<DayOfWeek>(d, CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(d)))];
        WeekdayToggles = new ObservableCollection<WeekdayToggle>(days.Select(d => new WeekdayToggle(d, Rebuild)));
        Months = [.. Enumerable.Range(1, 12).Select(m => new Choice<int>(m, CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(m)))];

        Frequency = Frequencies.First(f => f.Value == ScheduleFrequency.Monthly);
        End = Ends[0];
        Interval = 1;
        Count = 12;
        StartDate = start.ToDateTime(TimeOnly.MinValue);
        EndDate = start.AddYears(1).ToDateTime(TimeOnly.MinValue);
        Account = Accounts.FirstOrDefault(a => a.Id == (existing?.AccountId ?? draft?.AccountId)) ?? Accounts.FirstOrDefault();
        TransferAccount = TransferAccounts.FirstOrDefault(a => a.Id == existing?.TransferAccountId) ?? NoTransfer;
        Category = Categories.FirstOrDefault(c => c.Id == (existing?.CategoryId ?? draft?.CategoryId)) ?? RecurringItemEditorViewModel.NoCategory;
        Payee = existing?.PayeeName ?? draft?.Payee;
        var amount = existing?.Amount.Amount ?? draft?.Amount ?? 0;
        Amount = Math.Abs(amount);
        IsInflow = amount > 0;
        Memo = existing?.Memo;
        AutoEnter = existing?.AutoEnter ?? false;
        SetFromStart(start);
        if (existing is not null && RecurrenceRule.TryParse(existing.Rule, out var rule, out _))
        {
            StartDate = existing.NextDate.ToDateTime(TimeOnly.MinValue);
            LoadRule(rule, existing.EndDate);
        }
        else if (draft?.Rule is { } draftRule)
        {
            LoadRule(draftRule, null);
        }

        _loading = false;
        Rebuild();
    }

    /// <summary>"No transfer".</summary>
    public static AccountOption NoTransfer { get; } = new(Guid.Empty, Strings.Schedule_NoTransfer, true, false, Keel.Domain.Currency.Default);

    /// <inheritdoc />
    public override string Title => _existing is null ? Strings.Schedule_AddTitle : Strings.Schedule_EditTitle;

    /// <inheritdoc />
    public override double PreferredMaxWidth => 620;

    /// <summary>Whether this edits an existing schedule (shows Delete).</summary>
    public bool IsExisting => _existing is not null;

    /// <summary>The saved schedule; null after a delete.</summary>
    public ScheduledTransactionDto? Result { get; private set; }

    /// <summary>Whether the schedule was deleted.</summary>
    public bool Deleted { get; private set; }

    /// <summary>Accounts.</summary>
    public IReadOnlyList<AccountOption> Accounts { get; }

    /// <summary>Transfer targets ("No transfer" first).</summary>
    public IReadOnlyList<AccountOption> TransferAccounts { get; }

    /// <summary>Categories.</summary>
    public IReadOnlyList<CategoryOption> Categories { get; }

    /// <summary>Frequencies.</summary>
    public IReadOnlyList<Choice<ScheduleFrequency>> Frequencies { get; }

    /// <summary>End choices.</summary>
    public IReadOnlyList<Choice<ScheduleEnd>> Ends { get; }

    /// <summary>Days of month (1st … 31st, Last day).</summary>
    public IReadOnlyList<Choice<int>> MonthDays { get; }

    /// <summary>First … Fourth, Last.</summary>
    public IReadOnlyList<Choice<int>> Ordinals { get; }

    /// <summary>Weekdays for "the 2nd Tuesday".</summary>
    public IReadOnlyList<Choice<DayOfWeek>> Weekdays { get; }

    /// <summary>Weekday toggles of the weekly builder.</summary>
    public ObservableCollection<WeekdayToggle> WeekdayToggles { get; }

    /// <summary>Months of the yearly builder.</summary>
    public IReadOnlyList<Choice<int>> Months { get; }

    /// <summary>Account.</summary>
    [ObservableProperty]
    public partial AccountOption? Account { get; set; }

    /// <summary>Payee.</summary>
    [ObservableProperty]
    public partial string? Payee { get; set; }

    /// <summary>Amount (positive).</summary>
    [ObservableProperty]
    public partial long Amount { get; set; }

    /// <summary>Inflow rather than outflow.</summary>
    [ObservableProperty]
    public partial bool IsInflow { get; set; }

    /// <summary>Category.</summary>
    [ObservableProperty]
    public partial CategoryOption Category { get; set; }

    /// <summary>Transfer target, or <see cref="NoTransfer"/>.</summary>
    [ObservableProperty]
    public partial AccountOption TransferAccount { get; set; }

    /// <summary>Memo.</summary>
    [ObservableProperty]
    public partial string? Memo { get; set; }

    /// <summary>Enter automatically on the date (otherwise prompt).</summary>
    [ObservableProperty]
    public partial bool AutoEnter { get; set; }

    /// <summary>Frequency.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDaily), nameof(IsWeekly), nameof(IsMonthly), nameof(IsTwiceMonthly), nameof(IsYearly), nameof(HasInterval), nameof(IntervalUnit))]
    public partial Choice<ScheduleFrequency> Frequency { get; set; }

    /// <summary>Every N periods.</summary>
    [ObservableProperty]
    public partial decimal? Interval { get; set; }

    /// <summary>Monthly: on a weekday ("the 2nd Tuesday") instead of a day of the month.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMonthlyOnDay))]
    public partial bool MonthlyByWeekday { get; set; }

    /// <summary>Monthly and yearly: day of month.</summary>
    [ObservableProperty]
    public partial Choice<int> MonthDay { get; set; } = null!;

    /// <summary>Twice monthly: second day.</summary>
    [ObservableProperty]
    public partial Choice<int> SecondMonthDay { get; set; } = null!;

    /// <summary>Monthly by weekday: which one (1st … Last).</summary>
    [ObservableProperty]
    public partial Choice<int> Ordinal { get; set; } = null!;

    /// <summary>Monthly by weekday: the weekday.</summary>
    [ObservableProperty]
    public partial Choice<DayOfWeek> Weekday { get; set; } = null!;

    /// <summary>Yearly: month.</summary>
    [ObservableProperty]
    public partial Choice<int> Month { get; set; } = null!;

    /// <summary>First date.</summary>
    [ObservableProperty]
    public partial DateTime? StartDate { get; set; }

    /// <summary>How it ends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndsOnDate), nameof(EndsAfterCount))]
    public partial Choice<ScheduleEnd> End { get; set; }

    /// <summary>End date.</summary>
    [ObservableProperty]
    public partial DateTime? EndDate { get; set; }

    /// <summary>Number of times.</summary>
    [ObservableProperty]
    public partial decimal? Count { get; set; }

    /// <summary>The built rule text (canonical).</summary>
    [ObservableProperty]
    public partial string RuleText { get; private set; } = string.Empty;

    /// <summary>"Every month on the 15th, until 2027-12-31".</summary>
    [ObservableProperty]
    public partial string Description { get; private set; } = string.Empty;

    /// <summary>The next five dates.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> NextDates { get; private set; } = [];

    /// <summary>Why the rule is invalid, if it is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuleError))]
    public partial string? RuleError { get; private set; }

    /// <summary>Whether <see cref="RuleError"/> is set.</summary>
    public bool HasRuleError => !string.IsNullOrEmpty(RuleError);

    /// <summary>Daily.</summary>
    public bool IsDaily => Frequency.Value == ScheduleFrequency.Daily;

    /// <summary>Weekly.</summary>
    public bool IsWeekly => Frequency.Value == ScheduleFrequency.Weekly;

    /// <summary>Monthly.</summary>
    public bool IsMonthly => Frequency.Value == ScheduleFrequency.Monthly;

    /// <summary>Monthly on a day of the month.</summary>
    public bool IsMonthlyOnDay
    {
        get => !MonthlyByWeekday;
        set => MonthlyByWeekday = !value;
    }

    /// <summary>Twice monthly.</summary>
    public bool IsTwiceMonthly => Frequency.Value == ScheduleFrequency.TwiceMonthly;

    /// <summary>Yearly.</summary>
    public bool IsYearly => Frequency.Value == ScheduleFrequency.Yearly;

    /// <summary>Whether "every N" applies.</summary>
    public bool HasInterval => !IsTwiceMonthly;

    /// <summary>"days", "weeks", "months", "years".</summary>
    public string IntervalUnit => Label("Schedule_Unit" + Frequency.Value);

    /// <summary>Ends on a date.</summary>
    public bool EndsOnDate => End.Value == ScheduleEnd.OnDate;

    /// <summary>Ends after a count.</summary>
    public bool EndsAfterCount => End.Value == ScheduleEnd.AfterCount;

    /// <summary>Currency of the amount box.</summary>
    public string Currency => Account?.Currency ?? Keel.Domain.Currency.Default;

    /// <summary>Builds the rule from the builder fields; null when incomplete.</summary>
    public RecurrenceRule? BuildRule()
    {
        var interval = Math.Clamp((int)(Interval ?? 1), 1, RecurrenceRule.MaxInterval);
        var start = StartDate is { } s ? DateOnly.FromDateTime(s) : DateOnly.FromDateTime(DateTime.Today);
        RecurrenceRule rule;
        switch (Frequency.Value)
        {
            case ScheduleFrequency.Daily:
                rule = RecurrenceRule.Daily(interval);
                break;
            case ScheduleFrequency.Weekly:
                var days = WeekdayToggles.Where(t => t.IsChecked).Select(t => new WeekdayOccurrence(t.Day)).ToList();
                rule = RecurrenceRule.Create(RecurrenceFrequency.Weekly, interval, days.Count > 0 ? days : [new WeekdayOccurrence(start.DayOfWeek)]);
                break;
            case ScheduleFrequency.Monthly when MonthlyByWeekday:
                rule = RecurrenceRule.MonthlyOnWeekday(Ordinal.Value, Weekday.Value, interval);
                break;
            case ScheduleFrequency.Monthly:
                rule = RecurrenceRule.MonthlyOnDay(MonthDay.Value, interval);
                break;
            case ScheduleFrequency.TwiceMonthly:
                rule = MonthDay.Value == SecondMonthDay.Value
                    ? RecurrenceRule.MonthlyOnDay(MonthDay.Value)
                    : RecurrenceRule.TwiceMonthly(MonthDay.Value, SecondMonthDay.Value);
                break;
            default:
                rule = RecurrenceRule.Yearly(Month.Value, MonthDay.Value, interval);
                break;
        }

        return End.Value switch
        {
            ScheduleEnd.OnDate when EndDate is { } e => rule.WithUntil(DateOnly.FromDateTime(e)),
            ScheduleEnd.AfterCount => rule.WithCount(Math.Max(1, (int)(Count ?? 1))),
            _ => rule,
        };
    }

    /// <summary>Deletes the schedule (existing only).</summary>
    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (_existing is null)
        {
            return;
        }

        try
        {
            await _scheduled.DeleteAsync(_existing.Id, CancellationToken.None);
            Deleted = true;
            Close(true);
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        Rebuild();
        if (Account is null)
        {
            Error = Strings.Schedule_ErrorAccount;
            return false;
        }

        if (string.IsNullOrWhiteSpace(Payee))
        {
            Error = Strings.Bills_ErrorPayee;
            return false;
        }

        if (Amount <= 0)
        {
            Error = Strings.Bills_ErrorAmount;
            return false;
        }

        if (HasRuleError || StartDate is not { } start)
        {
            Error = RuleError ?? Strings.Bills_ErrorDate;
            return false;
        }

        try
        {
            var payee = await _payees.GetOrCreateAsync(Payee.Trim(), CancellationToken.None);
            var transfer = TransferAccount.Id == Guid.Empty ? (Guid?)null : TransferAccount.Id;
            var edit = new ScheduledTransactionEdit(
                Account.Id,
                payee.Id,
                IsInflow ? Amount : -Amount,
                Category.Id,
                transfer,
                Memo,
                RuleText,
                DateOnly.FromDateTime(start),
                null,
                AutoEnter,
                _existing is null ? _recurringItemId : null);
            Result = _existing is null
                ? await _scheduled.CreateAsync(edit, CancellationToken.None)
                : await _scheduled.UpdateAsync(_existing.Id, edit, CancellationToken.None);
            return true;
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            Error = ex.Message;
            return false;
        }
    }

    partial void OnAccountChanged(AccountOption? value) => OnPropertyChanged(nameof(Currency));

    partial void OnFrequencyChanged(Choice<ScheduleFrequency> value) => Rebuild();

    partial void OnIntervalChanged(decimal? value) => Rebuild();

    partial void OnMonthlyByWeekdayChanged(bool value) => Rebuild();

    partial void OnMonthDayChanged(Choice<int> value) => Rebuild();

    partial void OnSecondMonthDayChanged(Choice<int> value) => Rebuild();

    partial void OnOrdinalChanged(Choice<int> value) => Rebuild();

    partial void OnWeekdayChanged(Choice<DayOfWeek> value) => Rebuild();

    partial void OnMonthChanged(Choice<int> value) => Rebuild();

    partial void OnStartDateChanged(DateTime? value) => Rebuild();

    partial void OnEndChanged(Choice<ScheduleEnd> value) => Rebuild();

    partial void OnEndDateChanged(DateTime? value) => Rebuild();

    partial void OnCountChanged(decimal? value) => Rebuild();

    private static string Label(string key) => Strings.ResourceManager.GetString(key, Strings.Culture) ?? key;

    private static string OrdinalText(int day) => LedgerText.Format(Strings.Schedule_DayOrdinal, day, (day % 100) switch
    {
        11 or 12 or 13 => "th",
        _ => (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" },
    });

    private void SetFromStart(DateOnly start)
    {
        MonthDay = MonthDays.First(d => d.Value == start.Day);
        SecondMonthDay = MonthDays.First(d => d.Value == (start.Day <= 15 ? Math.Min(start.Day + 15, 28) : start.Day - 15));
        Ordinal = Ordinals.First(o => o.Value == Math.Min(((start.Day - 1) / 7) + 1, 4));
        Weekday = Weekdays.First(w => w.Value == start.DayOfWeek);
        Month = Months.First(m => m.Value == start.Month);
        foreach (var toggle in WeekdayToggles)
        {
            toggle.IsChecked = toggle.Day == start.DayOfWeek;
        }
    }

    private void LoadRule(RecurrenceRule rule, DateOnly? endDate)
    {
        Interval = rule.Interval;
        switch (rule.Frequency)
        {
            case RecurrenceFrequency.Daily:
                Frequency = Frequencies.First(f => f.Value == ScheduleFrequency.Daily);
                break;
            case RecurrenceFrequency.Weekly:
                Frequency = Frequencies.First(f => f.Value == ScheduleFrequency.Weekly);
                if (rule.ByDay.Count > 0)
                {
                    foreach (var toggle in WeekdayToggles)
                    {
                        toggle.IsChecked = rule.ByDay.Any(d => d.Day == toggle.Day);
                    }
                }

                break;
            case RecurrenceFrequency.Monthly when rule.ByMonthDay.Count == 2 && rule.Interval == 1:
                Frequency = Frequencies.First(f => f.Value == ScheduleFrequency.TwiceMonthly);
                MonthDay = MonthDays.FirstOrDefault(d => d.Value == rule.ByMonthDay[0]) ?? MonthDay;
                SecondMonthDay = MonthDays.FirstOrDefault(d => d.Value == rule.ByMonthDay[1]) ?? SecondMonthDay;
                break;
            case RecurrenceFrequency.Monthly:
                Frequency = Frequencies.First(f => f.Value == ScheduleFrequency.Monthly);
                if (rule.ByDay is [{ Ordinal: not 0 } byDay, ..])
                {
                    MonthlyByWeekday = true;
                    Ordinal = Ordinals.FirstOrDefault(o => o.Value == byDay.Ordinal) ?? Ordinal;
                    Weekday = Weekdays.First(w => w.Value == byDay.Day);
                }
                else if (rule.ByMonthDay.Count > 0)
                {
                    MonthDay = MonthDays.FirstOrDefault(d => d.Value == rule.ByMonthDay[0]) ?? MonthDay;
                }

                break;
            case RecurrenceFrequency.Yearly:
                Frequency = Frequencies.First(f => f.Value == ScheduleFrequency.Yearly);
                if (rule.ByMonth.Count > 0)
                {
                    Month = Months.First(m => m.Value == rule.ByMonth[0]);
                }

                if (rule.ByMonthDay.Count > 0)
                {
                    MonthDay = MonthDays.FirstOrDefault(d => d.Value == rule.ByMonthDay[0]) ?? MonthDay;
                }

                break;
        }

        var until = endDate ?? rule.Until;
        if (until is { } u)
        {
            End = Ends.First(e => e.Value == ScheduleEnd.OnDate);
            EndDate = u.ToDateTime(TimeOnly.MinValue);
        }
        else if (rule.Count is { } c)
        {
            End = Ends.First(e => e.Value == ScheduleEnd.AfterCount);
            Count = c;
        }
    }

    private void Rebuild()
    {
        if (_loading)
        {
            return;
        }

        RecurrenceRule? rule;
        try
        {
            rule = BuildRule();
        }
        catch (ArgumentException ex)
        {
            RuleError = ex.Message;
            Description = string.Empty;
            NextDates = [];
            return;
        }

        var start = StartDate is { } s ? DateOnly.FromDateTime(s) : DateOnly.FromDateTime(DateTime.Today);
        var check = _scheduled.CheckRule(rule!.ToString(), start, PreviewCount);
        RuleText = rule.ToString();
        RuleError = check.IsValid ? (check.NextDates.Count == 0 ? Strings.Schedule_ErrorNoDates : null) : check.Error;
        Description = check.Description ?? string.Empty;
        NextDates = check.NextDates.Select(BillsFormat.DayDate).ToList();
    }
}

/// <summary>Pre-filled values for a new schedule (from a recurring item or a register).</summary>
/// <param name="AccountId">Account.</param>
/// <param name="Payee">Payee.</param>
/// <param name="Amount">Signed amount.</param>
/// <param name="CategoryId">Category.</param>
/// <param name="Rule">Rule to start from.</param>
/// <param name="RecurringItemId">The item the schedule is created from (linked on save).</param>
public sealed record ScheduledDraft(Guid? AccountId, string? Payee, long Amount, Guid? CategoryId, RecurrenceRule? Rule, Guid? RecurringItemId);
