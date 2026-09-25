using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Keel.Desktop.ViewModels.FirstRun;

namespace Keel.Desktop.Views.FirstRun;

/// <summary>View for <see cref="FirstRunViewModel"/>: moves focus to the first field of each step.</summary>
public partial class FirstRunView : UserControl
{
    /// <summary>Creates the view.</summary>
    public FirstRunView()
    {
        InitializeComponent();
        BalanceBox.KeyDown += (_, e) =>
        {
            // Enter in the amount saves the evaluated amount (the box handles its own KeyDown first).
            if (e.Key is Key.Enter or Key.Return && DataContext is FirstRunViewModel vm)
            {
                Dispatcher.UIThread.Post(() => vm.AddAccountCommand.Execute(null));
            }
        };
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is FirstRunViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(FirstRunViewModel.Step))
                {
                    FocusStep(vm);
                }
            };
        }
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is FirstRunViewModel vm)
        {
            FocusStep(vm);
        }
    }

    private void FocusStep(FirstRunViewModel vm) => Dispatcher.UIThread.Post(
        () =>
        {
            Control target = vm.Step switch
            {
                Services.FirstRunStep.Template => TemplateList,
                Services.FirstRunStep.Account => AccountNameBox,
                _ => FileNameBox,
            };
            target.Focus(NavigationMethod.Tab);
        },
        DispatcherPriority.Background);
}
