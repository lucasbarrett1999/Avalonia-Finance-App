using ReactiveUI;
using System.Reactive; // Required for Unit in ReactiveCommand
using System.Reactive.Linq; // For OAPH
using System.Reactive.Subjects; // For BehaviorSubject
using MyApp.Core.Enums;
using MyApp.Core.Interfaces;
using Avalonia;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace MyApp.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IScreen
{
    // The Router associated with this Screen.
    // Required by the IScreen interface.
    public RoutingState Router { get; } = new RoutingState();

    // Theme Management
    private readonly BehaviorSubject<AppTheme> _currentAppThemeSubject;
    private readonly ObservableAsPropertyHelper<AppTheme> _currentAppThemeHelper;
    public AppTheme CurrentAppTheme => _currentAppThemeHelper.Value;
    public ReactiveCommand<AppTheme, Unit> SwitchThemeCommand { get; }

    private readonly ObservableAsPropertyHelper<bool> _isDarkThemeActiveHelper;
    public bool IsDarkThemeActive => _isDarkThemeActiveHelper.Value;

    // Sidebar Compact State - REMOVED
    // private readonly BehaviorSubject<bool> _isSidebarCompactSubject;
    // private readonly ObservableAsPropertyHelper<bool> _isSidebarCompactHelper;
    // public bool IsSidebarCompact => _isSidebarCompactHelper.Value;
    // public double FullSidebarWidth { get; } = 200;
    // public double CompactSidebarWidth { get; } = 50;
    // private readonly ObservableAsPropertyHelper<double> _currentSidebarWidthHelper;
    // public double CurrentSidebarWidth => _currentSidebarWidthHelper.Value;
    // public ReactiveCommand<Unit, Unit> ToggleSidebarCompactCommand { get; }

    public string Greeting { get; } = "Welcome to Avalonia! This is a test.";

    // Command to navigate to the Dashboard
    public ReactiveCommand<Unit, IRoutableViewModel> GoToDashboardCommand { get; }
    public ReactiveCommand<Unit, IRoutableViewModel> GoToAccountsCommand { get; }
    public ReactiveCommand<Unit, IRoutableViewModel> GoToBudgetsCommand { get; }
    public ReactiveCommand<Unit, IRoutableViewModel> GoToTransactionsCommand { get; }
    public ReactiveCommand<Unit, IRoutableViewModel> GoToGoalsCommand { get; }
    public ReactiveCommand<Unit, IRoutableViewModel> GoToPlaidLinkCommand { get; }

    // History Navigation Command
    public ReactiveCommand<Unit, IRoutableViewModel> GoBackCommand => Router.NavigateBack;
    
    // PlaidLink navigation command - takes service provider to create properly scoped ViewModel instance
    private readonly IServiceProvider? _serviceProvider;
    
    public MainWindowViewModel()
    {
        // Initialize theme
        AppTheme initialTheme = AppTheme.Default;
        if (Application.Current != null)
        {
            var currentVariant = Application.Current.ActualThemeVariant;
            if (currentVariant == ThemeVariant.Light)
            {
                initialTheme = AppTheme.Light;
            }
            else if (currentVariant == ThemeVariant.Dark)
            {
                initialTheme = AppTheme.Dark;
            }
            // Default is already AppTheme.Default, so no else needed for ThemeVariant.Default
        }
        _currentAppThemeSubject = new BehaviorSubject<AppTheme>(initialTheme);
        _currentAppThemeHelper = _currentAppThemeSubject
            .ToProperty(this, x => x.CurrentAppTheme);

        _isDarkThemeActiveHelper = _currentAppThemeSubject
            .Select(theme =>
            {
                if (theme == AppTheme.Dark) return true;
                if (theme == AppTheme.Light) return false;
                // AppTheme.Default: check system theme
                return Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
            })
            .ToProperty(this, x => x.IsDarkThemeActive);

        SwitchThemeCommand = ReactiveCommand.Create<AppTheme>(SwitchTheme);

        // Initialize Sidebar State - REMOVED
        // _isSidebarCompactSubject = new BehaviorSubject<bool>(false);
        // _isSidebarCompactHelper = _isSidebarCompactSubject.ToProperty(this, x => x.IsSidebarCompact);
        // _currentSidebarWidthHelper = _isSidebarCompactSubject
        //     .Select(isCompact => isCompact ? CompactSidebarWidth : FullSidebarWidth)
        //     .ToProperty(this, x => x.CurrentSidebarWidth);
        // ToggleSidebarCompactCommand = ReactiveCommand.Create(() =>
        // {
        //     _isSidebarCompactSubject.OnNext(!_isSidebarCompactSubject.Value);
        // });

        // Navigate to a default view
        Router.Navigate.Execute(new DashboardViewModel(this));

        // Initialize commands
        GoToDashboardCommand = ReactiveCommand.CreateFromObservable(
            () => Router.Navigate.Execute(new DashboardViewModel(this))
        );
        GoToAccountsCommand = ReactiveCommand.CreateFromObservable(
            () => Router.Navigate.Execute(CreateAccountsViewModel())
        );
        GoToBudgetsCommand = ReactiveCommand.CreateFromObservable(
            () => Router.Navigate.Execute(new BudgetsViewModel(this))
        );
        GoToTransactionsCommand = ReactiveCommand.CreateFromObservable(
            () => Router.Navigate.Execute(new TransactionsViewModel(this))
        );
        GoToGoalsCommand = ReactiveCommand.CreateFromObservable(
            () => Router.Navigate.Execute(new GoalsViewModel(this))
        );
    }
    
    // Constructor for DI, will be used by App.axaml.cs when creating the MainWindow
    public MainWindowViewModel(IServiceProvider serviceProvider) : this()
    {
        _serviceProvider = serviceProvider;
        
        // Initialize the PlaidLink navigation command
        GoToPlaidLinkCommand = ReactiveCommand.CreateFromObservable(
            () => Router.Navigate.Execute(CreatePlaidLinkViewModel())
        );
    }
    
    private IRoutableViewModel CreatePlaidLinkViewModel()
    {
        if (_serviceProvider == null)
        {
            throw new InvalidOperationException("Service provider is not available");
        }
        
        return new PlaidLinkViewModel(
            this,
            _serviceProvider.GetRequiredService<IPlaidService>(),
            _serviceProvider.GetRequiredService<IPlaidTokenManager>(),
            _serviceProvider.GetRequiredService<IAccountSyncService>()
        );
    }
    
    private IRoutableViewModel CreateAccountsViewModel()
    {
        if (_serviceProvider == null)
        {
            // Fallback to the simple constructor if service provider is not available
            return new AccountsViewModel(this);
        }
        
        try
        {
            // Use the DI constructor with all required services
            return new AccountsViewModel(
                this,
                _serviceProvider.GetRequiredService<IPlaidService>(),
                _serviceProvider.GetRequiredService<IPlaidTokenManager>(),
                _serviceProvider.GetRequiredService<IAccountSyncService>()
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating AccountsViewModel: {ex.Message}");
            // Fallback to the simple constructor
            return new AccountsViewModel(this);
        }
    }

    private void SwitchTheme(AppTheme theme)
    {
        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }
        _currentAppThemeSubject.OnNext(theme);
    }
}