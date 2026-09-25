using Keel.Application.Ledger;
using Keel.Application.Recurring;
using Keel.Application.Rules;
using Keel.Application.Tags;
using Keel.Application.Undo;
using Keel.Domain.Rules;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Tests.Tags;

/// <summary>F-TXN-8 tags: saving with the transaction, search and filter, rename, merge, delete, rules and undo.</summary>
public sealed class TagServiceTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    private static CancellationToken Ct => CancellationToken.None;

    private ITagService Tags => _host.Get<ITagService>();

    private IRuleService Rules => _host.Get<IRuleService>();

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<TransactionDto> AddTaggedAsync(Guid account, long amount, string payee, params string[] tags) =>
        await _host.Transactions.SaveAsync(new SaveTransactionRequest(null, account, new DateOnly(2026, 8, 10), amount, payee, null, null, Tags: tags), Ct);

    private async Task<IReadOnlyList<string>> TagsOfAsync(Guid id) => (await _host.Transactions.GetAsync(id, Ct))!.Tags;

    private async Task<RegisterRow> RowAsync(Guid id) =>
        (await _host.Register.GetPageAsync(new RegisterFilter(), RegisterSort.Default, 0, 200, Ct)).Single(r => r.Id == id);

    [Fact]
    public async Task Saving_creates_tags_case_insensitively_and_undo_removes_the_new_tag()
    {
        var checking = await _host.CheckingAsync();
        var first = await AddTaggedAsync(checking.Id, -1_000, "Hotel", "Trip 2026", "#work");
        first.Tags.ShouldBe(["Trip 2026", "work"]);

        var second = await AddTaggedAsync(checking.Id, -2_000, "Airline", "trip 2026", "TRIP 2026", "  ");
        second.Tags.ShouldBe(["Trip 2026"], "an existing tag is reused whatever its case; duplicates and blanks are dropped");
        (await Tags.GetTagsAsync(Ct)).Select(t => t.Name).ShouldBe(["Trip 2026", "work"]);
        (await RowAsync(first.Id)).Tags.ShouldBe(["Trip 2026", "work"]);

        // Null keeps the tags; a list replaces them exactly.
        await _host.Transactions.SaveAsync(new SaveTransactionRequest(first.Id, checking.Id, new DateOnly(2026, 8, 10), -1_100, "Hotel", null, null), Ct);
        (await TagsOfAsync(first.Id)).ShouldBe(["Trip 2026", "work"]);
        await _host.Transactions.SaveAsync(new SaveTransactionRequest(first.Id, checking.Id, new DateOnly(2026, 8, 10), -1_100, "Hotel", null, null, Tags: ["Receipts"]), Ct);
        (await TagsOfAsync(first.Id)).ShouldBe(["Receipts"]);

        _host.Undo.NextUndo.ShouldBe(LedgerAction.EditTransaction);
        await _host.Undo.UndoAsync(Ct);
        (await TagsOfAsync(first.Id)).ShouldBe(["Trip 2026", "work"]);
        (await Tags.GetTagsAsync(Ct)).Select(t => t.Name).ShouldBe(["Trip 2026", "work"], "the tag the edit created is undone with it");
    }

    [Fact]
    public async Task A_tag_only_edit_announces_the_transactions_account()
    {
        var checking = await _host.CheckingAsync();
        var row = await AddTaggedAsync(checking.Id, -1_000, "Hotel");
        _host.Bus.Messages.Clear();

        await _host.Transactions.SaveAsync(new SaveTransactionRequest(row.Id, checking.Id, row.Date, row.Amount, "Hotel", null, null, Tags: ["Trip"]), Ct);

        var changed = _host.Bus.LedgerChanges.ShouldHaveSingleItem();
        changed.AccountIds.ShouldBe([checking.Id]);
    }

    [Fact]
    public async Task Search_and_the_tag_filter_find_tagged_rows()
    {
        var checking = await _host.CheckingAsync();
        var trip = await AddTaggedAsync(checking.Id, -1_000, "Hotel", "Trip 2026");
        var work = await AddTaggedAsync(checking.Id, -2_000, "Airline", "Work");
        await AddTaggedAsync(checking.Id, -3_000, "Grocer");
        var tripTag = (await Tags.GetTagsAsync(Ct)).Single(t => t.Name == "Trip 2026");

        async Task<IReadOnlyList<Guid>> FindAsync(RegisterFilter filter) =>
            (await _host.Register.GetPageAsync(filter, RegisterSort.Default, 0, 200, Ct)).Select(r => r.Id).ToList();

        (await FindAsync(new RegisterFilter(Search: "tag:trip"))).ShouldBe([trip.Id]);
        (await FindAsync(new RegisterFilter(Search: "tag:\"trip 2026\""))).ShouldBe([trip.Id]);
        (await FindAsync(new RegisterFilter(Search: "tag:trip tag:work"))).ShouldBe([work.Id, trip.Id], ignoreOrder: true);
        (await FindAsync(new RegisterFilter(Search: "work"))).ShouldBe([work.Id], "free words search tag names too");
        (await FindAsync(new RegisterFilter(Search: "has:tag"))).ShouldBe([work.Id, trip.Id], ignoreOrder: true);
        (await FindAsync(new RegisterFilter(TagId: tripTag.Id))).ShouldBe([trip.Id]);
        (await _host.Register.CountAsync(new RegisterFilter(TagId: tripTag.Id, Search: "hotel"), Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Rename_follows_in_rules_and_refuses_taken_empty_and_reserved_names()
    {
        var checking = await _host.CheckingAsync();
        var row = await AddTaggedAsync(checking.Id, -1_000, "Hotel", "trip");
        await AddTaggedAsync(checking.Id, -1_000, "Grocer", "Food");
        var rule = await Rules.SaveAsync(new RuleDefinition
        {
            Name = "Trips",
            Conditions = new RuleConditionSet { Conditions = [new TagCondition("TRIP")] },
            Actions = new RuleActionSet { Actions = [new AddTagAction("trip"), new AddTagAction("other")] },
        }, Ct);
        var tags = await Tags.GetTagsAsync(Ct);
        var trip = tags.Single(t => t.Name == "trip");
        _host.Bus.Messages.Clear();

        var renamed = await Tags.RenameAsync(trip.Id, "Travel", Ct);

        renamed.ShouldBe(new TagDto(trip.Id, "Travel"));
        (await TagsOfAsync(row.Id)).ShouldBe(["Travel"]);
        var definition = (await Rules.GetAsync(rule.Id, Ct))!.Definition!;
        definition.Conditions.Conditions.ShouldBe([new TagCondition("Travel")]);
        definition.Actions.Actions.ShouldBe([new AddTagAction("Travel"), new AddTagAction("other")]);
        _host.Bus.Messages.OfType<RulesChanged>().ShouldNotBeEmpty();
        (await Tags.ListAsync(Ct)).Single(t => t.Id == trip.Id).RuleCount.ShouldBe(1);

        (await Should.ThrowAsync<LedgerValidationException>(() => Tags.RenameAsync(trip.Id, "food", Ct))).Error.ShouldBe(LedgerError.TagNameTaken);
        (await Should.ThrowAsync<LedgerValidationException>(() => Tags.RenameAsync(trip.Id, " # ", Ct))).Error.ShouldBe(LedgerError.TagNameRequired);
        (await Should.ThrowAsync<LedgerValidationException>(() => Tags.RenameAsync(trip.Id, "flagged", Ct))).Error.ShouldBe(LedgerError.TagReserved);
        (await Tags.RenameAsync(trip.Id, "TRAVEL", Ct)).Name.ShouldBe("TRAVEL", "a change of case is a rename");

        await _host.Undo.UndoAsync(Ct);
        await _host.Undo.UndoAsync(Ct);
        (await TagsOfAsync(row.Id)).ShouldBe(["trip"]);
        (await Rules.GetAsync(rule.Id, Ct))!.Definition!.Conditions.Conditions.ShouldBe([new TagCondition("TRIP")]);
    }

    [Fact]
    public async Task Merge_moves_links_rules_and_the_subscription_designation_and_undo_restores_all()
    {
        var checking = await _host.CheckingAsync();
        var both = await AddTaggedAsync(checking.Id, -1_000, "Streamer", "subs", "Subscriptions");
        var onlySource = await AddTaggedAsync(checking.Id, -2_000, "Music", "subs");
        var deleted = await AddTaggedAsync(checking.Id, -3_000, "Old", "subs");
        await _host.Transactions.DeleteAsync([deleted.Id], Ct);
        var tags = await Tags.GetTagsAsync(Ct);
        var source = tags.Single(t => t.Name == "subs");
        var target = tags.Single(t => t.Name == "Subscriptions");
        var recurring = _host.Get<IRecurringService>();
        await recurring.SetSubscriptionDesignationsAsync(new SubscriptionDesignations([], [source.Id]), Ct);
        var rule = await Rules.SaveAsync(new RuleDefinition
        {
            Name = "Subs",
            Conditions = new RuleConditionSet { Conditions = [new PayeeCondition(TextOperator.Contains, "music")] },
            Actions = new RuleActionSet { Actions = [new AddTagAction("Subs")] },
        }, Ct);

        var result = await Tags.MergeAsync(source.Id, target.Id, Ct);

        result.Target.ShouldBe(target);
        result.TransactionsMoved.ShouldBe(2, "the deleted row moves too; the row that had both is not counted");
        result.RulesUpdated.ShouldBe(1);
        (await TagsOfAsync(both.Id)).ShouldBe(["Subscriptions"]);
        (await TagsOfAsync(onlySource.Id)).ShouldBe(["Subscriptions"]);
        (await Tags.GetTagsAsync(Ct)).ShouldBe([target]);
        (await recurring.GetSubscriptionDesignationsAsync(Ct)).TagIds.ShouldBe([target.Id]);
        (await Rules.GetAsync(rule.Id, Ct))!.Definition!.Actions.Actions.ShouldBe([new AddTagAction("Subscriptions")]);
        await _host.Transactions.RestoreAsync([deleted.Id], Ct);
        (await TagsOfAsync(deleted.Id)).ShouldBe(["Subscriptions"]);
        await _host.Undo.UndoAsync(Ct);

        _host.Undo.NextUndo.ShouldBe(LedgerAction.MergeTags);
        await _host.Undo.UndoAsync(Ct);
        (await TagsOfAsync(both.Id)).ShouldBe(["subs", "Subscriptions"]);
        (await TagsOfAsync(onlySource.Id)).ShouldBe(["subs"]);
        (await recurring.GetSubscriptionDesignationsAsync(Ct)).TagIds.ShouldBe([source.Id]);
        (await Rules.GetAsync(rule.Id, Ct))!.Definition!.Actions.Actions.ShouldBe([new AddTagAction("Subs")]);

        (await Should.ThrowAsync<LedgerValidationException>(() => Tags.MergeAsync(source.Id, source.Id, Ct))).Error.ShouldBe(LedgerError.TagMergeSame);
    }

    [Fact]
    public async Task Delete_removes_the_tag_everywhere_with_a_count_and_undo_brings_it_back()
    {
        var checking = await _host.CheckingAsync();
        var a = await AddTaggedAsync(checking.Id, -1_000, "Hotel", "Trip", "Work");
        var b = await AddTaggedAsync(checking.Id, -2_000, "Airline", "Trip");
        var gone = await AddTaggedAsync(checking.Id, -3_000, "Taxi", "Trip");
        await _host.Transactions.DeleteAsync([gone.Id], Ct);
        var trip = (await Tags.GetTagsAsync(Ct)).Single(t => t.Name == "Trip");
        (await Tags.ListAsync(Ct)).Single(t => t.Id == trip.Id).TransactionCount.ShouldBe(2, "deleted transactions are not counted");
        var recurring = _host.Get<IRecurringService>();
        await recurring.SetSubscriptionDesignationsAsync(new SubscriptionDesignations([], [trip.Id]), Ct);

        (await Tags.DeleteAsync(trip.Id, Ct)).ShouldBe(2);

        (await TagsOfAsync(a.Id)).ShouldBe(["Work"]);
        (await TagsOfAsync(b.Id)).ShouldBeEmpty();
        (await recurring.GetSubscriptionDesignationsAsync(Ct)).TagIds.ShouldBeEmpty();
        await using (var db = _host.Db())
        {
            (await db.TransactionTags.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
        }

        _host.Undo.NextUndo.ShouldBe(LedgerAction.DeleteTag);
        await _host.Undo.UndoAsync(Ct);
        (await TagsOfAsync(a.Id)).ShouldBe(["Trip", "Work"]);
        (await TagsOfAsync(gone.Id)).ShouldBe(["Trip"], "links of deleted transactions come back too");
        (await recurring.GetSubscriptionDesignationsAsync(Ct)).TagIds.ShouldBe([trip.Id]);
    }

    [Fact]
    public async Task The_flag_of_rules_keeps_working_and_flagged_cannot_be_renamed_or_merged()
    {
        var checking = await _host.CheckingAsync();
        var row = await AddTaggedAsync(checking.Id, -9_000, "Big Store");
        var rule = await Rules.SaveAsync(new RuleDefinition
        {
            Name = "Flag big",
            Conditions = new RuleConditionSet { Conditions = [new AmountCondition(AmountOperator.GreaterThan, 5_000)] },
            Actions = new RuleActionSet { Actions = [new FlagAction()] },
        }, Ct);
        await Rules.ApplyRetroactivelyAsync([rule.Id], RetroactiveScope.All, Ct);
        (await TagsOfAsync(row.Id)).ShouldBe(["Flagged"]);
        var flagged = (await Tags.ListAsync(Ct)).Single();
        flagged.IsReserved.ShouldBeTrue();

        // The editor saves the chips it shows, Flagged included, and adding a tag keeps the flag.
        await _host.Transactions.SaveAsync(new SaveTransactionRequest(row.Id, checking.Id, row.Date, row.Amount, "Big Store", null, null, Tags: ["flagged", "Tax"]), Ct);
        (await TagsOfAsync(row.Id)).ShouldBe(["Flagged", "Tax"]);
        (await Rules.PreviewRetroactiveAsync([rule.Id], RetroactiveScope.All, Ct)).Changes.ShouldBeEmpty("the row is still flagged");

        var tax = (await Tags.GetTagsAsync(Ct)).Single(t => t.Name == "Tax");
        (await Should.ThrowAsync<LedgerValidationException>(() => Tags.RenameAsync(flagged.Id, "Important", Ct))).Error.ShouldBe(LedgerError.TagReserved);
        (await Should.ThrowAsync<LedgerValidationException>(() => Tags.MergeAsync(tax.Id, flagged.Id, Ct))).Error.ShouldBe(LedgerError.TagReserved);
        (await Should.ThrowAsync<LedgerValidationException>(() => Tags.MergeAsync(flagged.Id, tax.Id, Ct))).Error.ShouldBe(LedgerError.TagReserved);
    }

    [Fact]
    public async Task Purging_a_deleted_transaction_removes_its_tags_and_undo_restores_them()
    {
        var checking = await _host.CheckingAsync();
        var row = await AddTaggedAsync(checking.Id, -1_000, "Hotel", "Trip");
        await _host.Transactions.DeleteAsync([row.Id], Ct);

        await _host.Transactions.PurgeDeletedAsync([row.Id], Ct);
        await using (var db = _host.Db())
        {
            (await db.TransactionTags.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        }

        await _host.Undo.UndoAsync(Ct);
        (await TagsOfAsync(row.Id)).ShouldBe(["Trip"]);
    }
}
