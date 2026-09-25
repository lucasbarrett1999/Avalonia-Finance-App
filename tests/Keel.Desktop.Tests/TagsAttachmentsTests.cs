using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Attachments;
using Keel.Application.Ledger;
using Keel.Application.Payees;
using Keel.Application.Tags;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Register;
using Keel.Desktop.ViewModels.Rules;
using Keel.Desktop.ViewModels.Settings;
using Keel.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop.Tests;

/// <summary>M9c headless flows: the tag box, tag chips and filter, attachments, Settings → Tags and payee merge.</summary>
public sealed class TagsAttachmentsTests : IDisposable
{
    private readonly FakeAttachmentFiles _files = new();
    private readonly TestHost _host;

    public TagsAttachmentsTests() => _host = TestHost.Create(services => services.AddSingleton<IAttachmentFiles>(_files));

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    private async Task<(ShellWindow Window, ShellViewModel Shell, AccountsViewModel Vm, AccountsView View)> OpenRegisterAsync(Guid accountId)
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        shell.OpenAccount(accountId);
        var vm = _host.Get<AccountsViewModel>();
        await vm.SettleAsync();
        return (window, shell, vm, window.Register());
    }

    private static async Task<RegisterRowViewModel> SelectAsync(AccountsView view, AccountsViewModel vm, Guid id)
    {
        await UiTestHelpers.WaitUntilAsync(() => vm.Rows.LoadedRows.Any(r => r.Id == id), "row loaded");
        var row = vm.Rows.LoadedRows.First(r => r.Id == id);
        view.Grid.SelectedItem = row;
        view.Grid.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        return row;
    }

    private async Task<IReadOnlyList<string>> TagsOfAsync(Guid id) => (await Task.Run(() => _host.Get<ITransactionService>().GetAsync(id, Ct)))!.Tags;

    [AvaloniaFact]
    public async Task T_opens_the_tag_box_where_Enter_adds_Backspace_removes_and_Enter_saves()
    {
        var ledger = await TagTestLedger.CreateAsync(_host);
        var (window, shell, vm, view) = await OpenRegisterAsync(ledger.Checking);
        var grocer = await SelectAsync(view, vm, ledger.Grocer);
        grocer.HasTags.ShouldBeFalse();

        window.Press(PhysicalKey.T);
        await UiTestHelpers.WaitUntilAsync(
            () => vm.IsEditing && (window.Focused() as Control)?.FindAncestorOfType<AutoCompleteBox>()?.Name == "EditorTags", "tag box focused");
        vm.Editor!.Tags.Known.ShouldBe(["Flagged", "Trip 2026", "Work"]);

        window.Type("trip 2026");
        window.Press(PhysicalKey.Enter);
        vm.IsEditing.ShouldBeTrue("Enter with text adds a tag instead of saving");
        vm.Editor.Tags.Chips.Select(c => c.Name).ShouldBe(["Trip 2026"], "an existing tag keeps its spelling");
        window.Type("Receipts");
        window.Press(PhysicalKey.Enter);
        window.Type("Oops");
        window.Press(PhysicalKey.Enter);
        vm.Editor.Tags.Chips.Select(c => c.Name).ShouldBe(["Trip 2026", "Receipts", "Oops"]);
        window.Press(PhysicalKey.Backspace);
        vm.Editor.Tags.Chips.Select(c => c.Name).ShouldBe(["Trip 2026", "Receipts"]);

        window.Press(PhysicalKey.Enter);
        await UiTestHelpers.WaitUntilAsync(() => !vm.IsEditing, "saved");
        await vm.SettleAsync();
        (await TagsOfAsync(ledger.Grocer)).ShouldBe(["Receipts", "Trip 2026"]);
        await UiTestHelpers.WaitUntilAsync(() => vm.Rows.LoadedRows.First(r => r.Id == ledger.Grocer).HasTags, "chips shown");
        vm.Rows.LoadedRows.First(r => r.Id == ledger.Grocer).Tags.ShouldBe(["Receipts", "Trip 2026"]);
        vm.Rows.LoadedRows.First(r => r.Id == ledger.Grocer).AutomationName.ShouldContain("tags Receipts, Trip 2026");
        view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tagChip")).ShouldNotBeEmpty();

        // The new tag was created in the same action as the save: one undo removes both.
        shell.UndoCommand.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => vm.TagFilters.Count == 4, "tag list refreshed after undo");
        (await TagsOfAsync(ledger.Grocer)).ShouldBeEmpty();
        vm.TagFilters.Select(t => t.Name).ShouldBe([TagOption.All.Name, "Flagged", "Trip 2026", "Work"]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_tag_filter_and_tag_search_narrow_the_register()
    {
        var ledger = await TagTestLedger.CreateAsync(_host);
        var (window, _, vm, view) = await OpenRegisterAsync(ledger.Checking);
        vm.HasTagFilters.ShouldBeTrue();
        view.Named<ComboBox>("TagFilterBox").IsVisible.ShouldBeTrue();

        vm.SelectedTagFilter = vm.TagFilters.Single(t => t.Name == "Trip 2026");
        await vm.SettleAsync();
        vm.RowCount.ShouldBe(2);
        vm.HasActiveFilters.ShouldBeTrue();

        vm.ClearFiltersCommand.Execute(null);
        vm.SearchText = "tag:work";
        await vm.SettleAsync();
        vm.RowCount.ShouldBe(1);
        vm.SearchText = "has:attachment";
        await vm.SettleAsync();
        vm.RowCount.ShouldBe(1);
        await UiTestHelpers.WaitUntilAsync(() => vm.Rows.LoadedRows.Any(r => r.Id == ledger.Hotel), "hotel loaded");
        var hotel = vm.Rows.LoadedRows.Single(r => r.Id == ledger.Hotel);
        hotel.HasAttachments.ShouldBeTrue();
        hotel.AttachmentTip.ShouldBe("1 attachment(s)");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Files_attach_open_and_remove_from_the_editor_with_undo()
    {
        var ledger = await TagTestLedger.CreateAsync(_host);
        var (window, shell, vm, view) = await OpenRegisterAsync(ledger.Checking);
        await SelectAsync(view, vm, ledger.Airline);
        await vm.EditSelectedAsync();
        var attachments = vm.Editor!.Attachments.ShouldNotBeNull();
        attachments.Items.ShouldBeEmpty();

        _files.ToPick.Add(ledger.File("boarding-pass.png", "png bytes"));
        await UiTestHelpers.WaitUntilAsync(() => view.FindNamed<Button>("EditorAttach") is not null, "attach button realized");
        view.Named<Button>("EditorAttach").Command!.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => attachments.Items.Count == 1, "attached");
        await attachments.Working;
        var row = attachments.Items.Single();
        row.FileName.ShouldBe("boarding-pass.png");
        row.Detail.ShouldBe("9 bytes");
        shell.Status.CanUndo.ShouldBeTrue();

        attachments.OpenCommand.Execute(row);
        await attachments.Working;
        var opened = _files.Opened.ShouldHaveSingleItem();
        Path.GetFileName(opened).ShouldBe("boarding-pass.png");
        File.ReadAllText(opened).ShouldBe("png bytes");

        attachments.RemoveCommand.Execute(row);
        await attachments.Working;
        attachments.Items.ShouldBeEmpty();
        shell.UndoCommand.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => shell.Status.Message.Length > 0, "undone");
        await vm.SettleAsync();
        (await Task.Run(() => _host.Get<IAttachmentService>().ListAsync(ledger.Airline, Ct))).ShouldHaveSingleItem().FileName.ShouldBe("boarding-pass.png");
        await UiTestHelpers.WaitUntilAsync(() => vm.Rows.LoadedRows.First(r => r.Id == ledger.Airline).AttachmentCount == 1, "row shows the paperclip");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Files_dropped_on_a_new_transaction_are_attached_when_it_is_saved()
    {
        var ledger = await TagTestLedger.CreateAsync(_host);
        var (window, _, vm, _) = await OpenRegisterAsync(ledger.Checking);
        await vm.NewTransactionAsync();
        var editor = vm.Editor!;
        editor.Payee = "Parking Garage";
        editor.Outflow = 1_200;
        editor.Tags.Text = "Trip 2026";
        await editor.Attachments!.AddFilesAsync([ledger.File("parking.jpg", "jpg")]);
        editor.Attachments.Items.ShouldHaveSingleItem().IsPending.ShouldBeTrue();

        await vm.SaveCommand.ExecuteAsync(null);
        await vm.SettleAsync();

        var saved = vm.Selection.ShouldHaveSingleItem();
        (await TagsOfAsync(saved.Id)).ShouldBe(["Trip 2026"], "text typed in the tag box is saved too");
        (await Task.Run(() => _host.Get<IAttachmentService>().ListAsync(saved.Id, Ct))).ShouldHaveSingleItem().FileName.ShouldBe("parking.jpg");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Settings_tags_rename_merge_delete_and_show_transactions()
    {
        var ledger = await TagTestLedger.CreateAsync(_host);
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        shell.NavigateToSettings("Tags");
        var tags = _host.Get<TagsSettingsViewModel>();
        await UiTestHelpers.WaitUntilAsync(() => tags.Tags.Count == 3, "tags loaded");
        await tags.Loading;
        var trip = tags.Tags.Single(t => t.Name == "Trip 2026");
        trip.CountText.ShouldBe("2 transaction(s)");
        trip.RuleText.ShouldBe("Used by 1 rule(s)");
        tags.Tags.Single(t => t.Name == "Flagged").CanChange.ShouldBeFalse();
        window.GetVisualDescendants().OfType<Keel.Desktop.Views.Settings.TagsSettingsView>().ShouldHaveSingleItem();

        var renaming = tags.RenameCommand.ExecuteAsync(trip);
        var rename = await ImportDialogTests.DialogAsync<RenameTagDialogViewModel>(shell);
        rename.Name = "Work";
        await rename.ConfirmCommand.ExecuteAsync(null);
        rename.Error.ShouldBe("Another tag already has that name. Merge the two tags instead.");
        rename.Name = "Summer trip";
        await rename.ConfirmCommand.ExecuteAsync(null);
        await renaming;
        await UiTestHelpers.WaitUntilAsync(() => tags.Tags.Any(t => t.Name == "Summer trip"), "renamed");
        (await TagsOfAsync(ledger.Airline)).ShouldBe(["Summer trip"]);

        var work = tags.Tags.Single(t => t.Name == "Work");
        var merging = tags.MergeCommand.ExecuteAsync(work);
        var merge = await ImportDialogTests.DialogAsync<MergeTagDialogViewModel>(shell);
        merge.Targets.Select(t => t.Name).ShouldBe(["Summer trip"], "Flagged is not a merge target");
        merge.Message.ShouldContain("The 1 transaction(s) tagged \"Work\" get \"Summer trip\"");
        await merge.ConfirmCommand.ExecuteAsync(null);
        await merging;
        await UiTestHelpers.WaitUntilAsync(() => tags.Tags.Count == 2, "merged");
        (await TagsOfAsync(ledger.Hotel)).ShouldBe(["Summer trip"]);

        var summer = tags.Tags.Single(t => t.Name == "Summer trip");
        var deleting = tags.DeleteCommand.ExecuteAsync(summer);
        var confirm = await ImportDialogTests.DialogAsync<ConfirmDialogViewModel>(shell);
        confirm.Message.ShouldContain("Remove \"Summer trip\" from 2 transaction(s)");
        confirm.Message.ShouldContain("1 rule(s) still add this tag");
        confirm.ConfirmCommand.Execute(null);
        await deleting;
        await UiTestHelpers.WaitUntilAsync(() => tags.Tags.Count == 1, "deleted");
        shell.Status.CanUndo.ShouldBeTrue();
        shell.UndoCommand.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => tags.Tags.Count == 2, "delete undone");

        tags.ShowTransactionsCommand.Execute(tags.Tags.Single(t => t.Name == "Summer trip"));
        var register = _host.Get<AccountsViewModel>();
        await register.SettleAsync();
        shell.CurrentPage.ShouldBe(register);
        register.IsAllAccounts.ShouldBeTrue();
        register.SelectedTagFilter.Name.ShouldBe("Summer trip");
        register.RowCount.ShouldBe(2);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Settings_payees_merge_checked_payees_into_one_with_counts_and_undo()
    {
        await TagTestLedger.CreateAsync(_host);
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        shell.NavigateToSettings("Payees");
        var payees = _host.Get<PayeesViewModel>();
        await payees.EnsureLoadedAsync();
        payees.SearchText = "am";
        await payees.Loading;
        payees.Payees.Select(p => p.Name).ShouldBe(["Amazon", "Amazon.com", "AMZN Mktp"], ignoreOrder: true);
        payees.MergeCommand.CanExecute(null).ShouldBeFalse();

        foreach (var row in payees.Payees)
        {
            row.IsSelected = true;
        }

        payees.SelectionText.ShouldBe("3 selected");
        payees.MergeCommand.CanExecute(null).ShouldBeTrue();
        var merging = payees.MergeCommand.ExecuteAsync(null);
        var dialog = await ImportDialogTests.DialogAsync<MergePayeesDialogViewModel>(shell);
        await dialog.Refreshing;
        dialog.Choices.Single(c => c.Payee.Name == "AMZN Mktp").IsChecked = true;
        await dialog.Refreshing;
        dialog.Survivor.Name.ShouldBe("AMZN Mktp");
        dialog.Summary.ShouldBe("Merge 2 payee(s) into AMZN Mktp: 2 transaction(s), 0 scheduled transaction(s), 0 recurring item(s) and 0 rule(s) move.");
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<Keel.Desktop.Views.Rules.MergePayeesDialogView>().ShouldHaveSingleItem();
        await dialog.ConfirmCommand.ExecuteAsync(null);
        await merging;
        shell.Status.Message.ShouldBe("Merged 2 payee(s) into AMZN Mktp; 2 transaction(s) moved.");
        await UiTestHelpers.WaitUntilAsync(() => payees.Payees.Count == 1, "list refreshed");
        payees.HasSelection.ShouldBeFalse();
        (await Task.Run(() => _host.Get<IPayeeService>().ListAsync("am", 10, Ct))).Single().TransactionCount.ShouldBe(3);

        shell.UndoCommand.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => payees.Payees.Count == 3, "merge undone");
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_palette_offers_tags_payee_merge_and_attach_for_the_selected_row()
    {
        var ledger = await TagTestLedger.CreateAsync(_host);
        var (window, shell, vm, view) = await OpenRegisterAsync(ledger.Checking);
        var commands = _host.Get<AppCommands>();
        commands.Build(shell).Select(c => c.Id).ShouldContain("manage-tags");
        commands.Build(shell).Select(c => c.Id).ShouldContain("merge-payees");
        commands.Build(shell).Single(c => c.Id == "attach-file").IsEnabled.ShouldBeFalse("nothing is selected yet");
        commands.Build(shell).Single(c => c.Id == "register-tags").IsEnabled.ShouldBeFalse("nothing is selected yet");
        await SelectAsync(view, vm, ledger.Grocer);
        var tagCommand = commands.Build(shell).Single(c => c.Id == "register-tags");
        tagCommand.Keys.ShouldBe("T");
        tagCommand.IsEnabled.ShouldBeTrue();
        commands.Build(shell).Single(c => c.Id == "attach-file").Execute();
        await UiTestHelpers.WaitUntilAsync(() => vm.IsEditing, "editor opened for attaching");
        commands.Build(shell).Single(c => c.Id == "manage-tags").Execute();
        Dispatcher.UIThread.RunJobs();
        _host.Get<SettingsViewModel>().RequestedSection.ShouldBe("Tags");
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_daily_maintenance_run_removes_old_unreferenced_attachment_files()
    {
        var ledger = await TagTestLedger.CreateAsync(_host);
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        var attachments = _host.Get<IAttachmentService>();
        var folder = attachments.Folder.ShouldNotBeNull();
        var stray = Path.Combine(folder, new string('a', 64));
        await File.WriteAllTextAsync(stray, "left behind by an interrupted attach");
        File.SetLastWriteTimeUtc(stray, DateTime.UtcNow.AddDays(-3));
        var kept = (await Task.Run(() => attachments.ListAsync(ledger.Hotel, Ct))).Single();

        await shell.Maintenance.ShouldNotBeNull().RunAsync();

        File.Exists(stray).ShouldBeFalse();
        File.Exists(Path.Combine(folder, kept.Sha256)).ShouldBeTrue();
        window.Close();
    }
}
