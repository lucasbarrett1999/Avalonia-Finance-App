using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Keel.Desktop.Services;
using Keel.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop;

/// <summary>The Avalonia application.</summary>
public partial class App : Avalonia.Application
{
    /// <summary>The DI container, set by <see cref="Program"/> before the app starts.</summary>
    public static IServiceProvider? Services { get; set; }

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (Services is { } services)
        {
            services.GetRequiredService<ThemeService>().ApplySaved();
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = services.GetRequiredService<ShellWindow>();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
