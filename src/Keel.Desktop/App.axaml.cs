using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
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

            // macOS opens documents with an Apple Event rather than a command-line argument: a double-clicked
            // .keel file (Info.plist declares the type, build/package.sh) goes to the running app (PRD 8).
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable && services.GetService<BudgetSessions>() is { } sessions)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e is FileActivatedEventArgs { Files: { Count: > 0 } files } && files[0].TryGetLocalPath() is { } path
                        && path.EndsWith(Keel.Application.Files.IBudgetFileService.Extension, StringComparison.OrdinalIgnoreCase))
                    {
                        _ = sessions.ActivateAsync(path);
                    }
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
