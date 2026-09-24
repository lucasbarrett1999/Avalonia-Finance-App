using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Keel.Desktop.ViewModels.Sync;

/// <summary>Opens a URL in the user's default browser (Plaid Hosted Link). Tests replace it.</summary>
public interface IBrowserLauncher
{
    /// <summary>Opens <paramref name="url"/>; false when the platform could not.</summary>
    Task<bool> OpenAsync(Uri url);
}

/// <summary>Opens URLs through Avalonia's platform launcher (PRD 8: no <c>Process.Start</c> on URLs).</summary>
public sealed class AvaloniaBrowserLauncher : IBrowserLauncher
{
    /// <inheritdoc />
    public async Task<bool> OpenAsync(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var launcher = window is null ? null : TopLevel.GetTopLevel(window)?.Launcher;
        return launcher is not null && await launcher.LaunchUriAsync(url);
    }
}
