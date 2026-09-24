using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Keel.Application.Rules;
using Keel.Application.Settings;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Rules;
using Keel.Desktop.Views;
using Keel.Domain.Rules;

namespace Keel.Desktop.Tests;

/// <summary>Renders the M4 screens (Review with suggestions, Rules, rule editor, retroactive preview) in both themes.</summary>
public sealed class ReviewRulesRenderingTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private static void Save(ShellWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame()!;
        frame.PixelSize.Width.ShouldBeGreaterThan(0);
        var dir = Environment.GetEnvironmentVariable("KEEL_SCREENSHOT_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, name + ".png"));
        }
    }

    [AvaloniaFact]
    public async Task Review_and_rules_screens_render_in_light_and_dark_without_binding_errors()
    {
        var ledger = await ReviewTestLedger.CreateAsync(_host);
        await Task.Run(() => _host.Get<IRuleService>().SaveAsync(new RuleDefinition
        {
            Name = "Amazon purchases",
            ContinueAfterMatch = true,
            Conditions = new RuleConditionSet { Conditions = [new PayeeCondition(TextOperator.StartsWith, "AMZN", Normalized: true), new DirectionCondition(TransactionDirection.Outflow)] },
            Actions = new RuleActionSet { Actions = [new SetPayeeAction("Amazon"), new AddTagAction("online")] },
        }, CancellationToken.None));
        await Task.Run(() => _host.Get<IRuleService>().SaveAsync(new RuleDefinition
        {
            Name = "Costco → Household",
            Conditions = new RuleConditionSet { Conditions = [new PayeeCondition(TextOperator.Contains, "costco")] },
            Actions = new RuleActionSet { Actions = [new SetCategoryAction(ledger.Household)] },
        }, CancellationToken.None));
        LogCapture.Instance.Clear();

        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var themes = _host.Get<ThemeService>();
        var review = _host.Get<ReviewViewModel>();
        var rules = _host.Get<RulesViewModel>();

        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            themes.SetTheme(theme);

            shell.ReviewItem.NavigateCommand.Execute(null);
            await ReviewTests.SettleAsync(review);
            await review.FocusIndexAsync(0);
            await ReviewTests.SettleAsync(review);
            review.Suggestions.ShouldNotBeEmpty();
            review.ShowTrace = true;
            Save(window, $"Review-{theme}");
            review.ShowTrace = false;

            review.ManageRulesCommand.Execute(null);
            await rules.Loading;
            Dispatcher.UIThread.RunJobs();
            rules.Rules.Count.ShouldBe(2);
            Save(window, $"Rules-{theme}");

            var editing = rules.EditCommand.ExecuteAsync(rules.Rules[0]);
            await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is RuleEditorViewModel, "editor open");
            var editor = (RuleEditorViewModel)shell.Dialogs.Current!;
            editor.AddActionCommand.Execute(null);
            await editor.TestCommand.ExecuteAsync(null);
            Save(window, $"RuleEditor-{theme}");
            editor.CancelCommand.Execute(null);
            await editing;

            var applying = rules.ApplyToExistingCommand.ExecuteAsync(rules.Rules[1]);
            await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is RetroactiveApplyViewModel, "preview open");
            var preview = (RetroactiveApplyViewModel)shell.Dialogs.Current!;
            await preview.Loading;
            preview.Changes.ShouldNotBeEmpty();
            Save(window, $"RetroactivePreview-{theme}");
            preview.CancelCommand.Execute(null);
            await applying;

            shell.SettingsItem.NavigateCommand.Execute(null);
            await _host.Get<PayeesViewModel>().EnsureLoadedAsync();
            Dispatcher.UIThread.RunJobs();
            Save(window, $"SettingsRulesPayees-{theme}");
        }

        themes.SetTheme(AppTheme.System);
        window.Close();
        LogCapture.Instance.Messages.ShouldBeEmpty();
    }
}
