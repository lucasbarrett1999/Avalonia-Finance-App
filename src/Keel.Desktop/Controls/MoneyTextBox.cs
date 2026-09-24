using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Keel.Domain;
using Keel.Domain.Ledger;

namespace Keel.Desktop.Controls;

/// <summary>
/// An amount field (PRD 7.3) bound to <see cref="long"/> minor units. It parses the user's locale,
/// accepts a currency symbol, and evaluates inline arithmetic when the edit is committed (Enter or
/// focus leaving): typing <c>12.50+3</c> yields 15.50. Invalid input keeps the text, shows the
/// <c>:error</c> state and leaves <see cref="Value"/> unchanged. Rounding is banker's rounding to
/// the currency's minor unit.
/// </summary>
public class MoneyTextBox : TextBox
{
    /// <summary>Defines <see cref="Value"/> (two-way by default).</summary>
    public static readonly StyledProperty<long> ValueProperty =
        AvaloniaProperty.Register<MoneyTextBox, long>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Defines <see cref="Currency"/>.</summary>
    public static readonly StyledProperty<string> CurrencyProperty =
        AvaloniaProperty.Register<MoneyTextBox, string>(nameof(Currency), Keel.Domain.Currency.Default);

    /// <summary>Defines <see cref="ShowZeroAsEmpty"/>.</summary>
    public static readonly StyledProperty<bool> ShowZeroAsEmptyProperty =
        AvaloniaProperty.Register<MoneyTextBox, bool>(nameof(ShowZeroAsEmpty), true);

    /// <summary>Defines <see cref="Culture"/>.</summary>
    public static readonly StyledProperty<CultureInfo?> CultureProperty =
        AvaloniaProperty.Register<MoneyTextBox, CultureInfo?>(nameof(Culture));

    /// <summary>Defines <see cref="HasError"/>.</summary>
    public static readonly DirectProperty<MoneyTextBox, bool> HasErrorProperty =
        AvaloniaProperty.RegisterDirect<MoneyTextBox, bool>(nameof(HasError), o => o.HasError);

    private bool _hasError;
    private bool _updatingText;

    /// <summary>Creates the control.</summary>
    public MoneyTextBox()
    {
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        UpdateText();
    }

    /// <summary>Amount in minor units.</summary>
    public long Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>ISO 4217 currency code; sets the number of decimals.</summary>
    public string Currency
    {
        get => GetValue(CurrencyProperty);
        set => SetValue(CurrencyProperty, value);
    }

    /// <summary>Whether zero displays as an empty field (outflow/inflow columns).</summary>
    public bool ShowZeroAsEmpty
    {
        get => GetValue(ShowZeroAsEmptyProperty);
        set => SetValue(ShowZeroAsEmptyProperty, value);
    }

    /// <summary>Culture for parsing and formatting; null uses the current culture.</summary>
    public CultureInfo? Culture
    {
        get => GetValue(CultureProperty);
        set => SetValue(CultureProperty, value);
    }

    /// <summary>Whether the last committed text was not a valid amount.</summary>
    public bool HasError
    {
        get => _hasError;
        private set
        {
            SetAndRaise(HasErrorProperty, ref _hasError, value);
            PseudoClasses.Set(":error", value);
        }
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>
    /// Evaluates the text and updates <see cref="Value"/>. Returns false (and sets
    /// <see cref="HasError"/>) when the text is not a valid amount; empty text means zero.
    /// </summary>
    public bool Commit()
    {
        var culture = Culture ?? CultureInfo.CurrentCulture;
        var text = Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            HasError = false;
            SetCurrentValue(ValueProperty, 0L);
            UpdateText();
            return true;
        }

        if (!MoneyExpression.TryEvaluate(text, culture, out var result))
        {
            HasError = true;
            return false;
        }

        try
        {
            var money = Money.FromDecimal(result, Currency);
            HasError = false;
            SetCurrentValue(ValueProperty, money.Amount);
            UpdateText();
            return true;
        }
        catch (OverflowException)
        {
            HasError = true;
            return false;
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == CurrencyProperty
            || change.Property == ShowZeroAsEmptyProperty || change.Property == CultureProperty)
        {
            if (!IsFocused || change.Property != ValueProperty)
            {
                HasError = false;
                UpdateText();
            }
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key is Key.Enter or Key.Return)
        {
            // Commit first; the key keeps bubbling so a form can save on Enter.
            Commit();
        }

        base.OnKeyDown(e);
    }

    /// <inheritdoc />
    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        SelectAll();
    }

    /// <inheritdoc />
    protected override void OnLostFocus(RoutedEventArgs e)
    {
        Commit();
        base.OnLostFocus(e);
    }

    private void UpdateText()
    {
        if (_updatingText)
        {
            return;
        }

        _updatingText = true;
        try
        {
            var value = Value;
            SetCurrentValue(TextProperty, value == 0 && ShowZeroAsEmpty
                ? string.Empty
                : new Money(value, Keel.Domain.Currency.IsValidCode(Currency) ? Currency : Keel.Domain.Currency.Default)
                    .FormatNumber(Culture ?? CultureInfo.CurrentCulture));
        }
        finally
        {
            _updatingText = false;
        }
    }
}
