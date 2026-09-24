using Keel.Application.Accounts;
using Keel.Application.Import;
using Keel.Application.Ledger;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Import;
using Keel.Infrastructure.Import;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Tests.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static Keel.Infrastructure.Tests.Import.Pipeline.ImportKit;

namespace Keel.Infrastructure.Tests.Import.Pipeline;

/// <summary>
/// The persisted import pipeline (F-TXN-1, F-TXN-2 acceptance) against real SQLite files through
/// the app's DI graph.
/// </summary>
public sealed class ImportServiceTests : IAsyncLifetime
{
    private LedgerTestHost _host = null!;

    public async Task InitializeAsync() => _host = await LedgerTestHost.CreateAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private IImportService Import => _host.Get<IImportService>();

    public static TheoryData<string> Fixtures() => new(Directory.GetFiles(FixtureJson.OutputDirectory)
        .Select(Path.GetFileName).OfType<string>()
        .Where(n => !n.EndsWith(".json", StringComparison.Ordinal) && !n.StartsWith('.'))
        .Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Every_fixture_imports_completely_and_idempotently(string fixture)
    {
        var bytes = Fixture(fixture);
        var parsed = await _host.ParseAsync(fixture, bytes);
        var currency = parsed.Transactions[0].Currency;
        var account = await _host.Accounts.CreateAccountAsync(new CreateAccountRequest("Bank", AccountType.Checking, currency, new DateOnly(2025, 1, 1), 0), Ct);
        var batch = ImportBatchBuilder.FromParse(account.Id, currency, parsed);

        var first = await Import.ImportTransactionsAsync(TransactionSource.File, batch, Ct);
        first.Added.ShouldBe(batch.Transactions.Count);
        (await _host.CountAsync(account.Id)).ShouldBe(batch.Transactions.Count);

        var second = await Import.ImportTransactionsAsync(TransactionSource.File, batch, Ct);
        second.Added.ShouldBe(0);
        second.Updated.ShouldBe(0);
        second.DuplicatesSkipped.ShouldBe(batch.Transactions.Count);
        (await _host.CountAsync(account.Id)).ShouldBe(batch.Transactions.Count);

        // Stored rows keep the file's values and are unapproved, cleared imports.
        var stored = await _host.ImportedAsync(account.Id);
        stored.Sum(t => t.Amount).ShouldBe(batch.Transactions.Sum(t => t.Amount));
        stored.ShouldAllBe(t => t.Source == TransactionSource.File && !t.IsApproved && t.ImportFingerprint != null);
    }

    [Fact]
    public async Task Importing_the_same_csv_twice_adds_nothing_the_second_time()
    {
        var account = await _host.CheckingAsync();
        var csv = Utf8("""
            Date,Description,Amount
            2026-08-03,SQ *BLUE BOTTLE COFFEE,-5.75
            2026-08-03,SQ *BLUE BOTTLE COFFEE,-5.75
            2026-08-04,PAYROLL ACME CORP,2500.00
            """);

        var first = await _host.ImportFileAsync(account, "bank.csv", csv);
        var second = await _host.ImportFileAsync(account, "bank.csv", csv);

        first.Added.ShouldBe(3, "two identical coffees are two purchases");
        second.Added.ShouldBe(0);
        second.DuplicatesSkipped.ShouldBe(3);
        (await _host.CountAsync(account.Id)).ShouldBe(3);
    }

    [Fact]
    public async Task Ofx_rows_dedup_by_fitid_and_update_in_place()
    {
        var account = await _host.CheckingAsync();
        var original = Ofx(("F1", "20260805", "-12.50", "CORNER STORE"), ("F2", "20260806", "-40.00", "GAS STATION"));
        (await _host.ImportFileAsync(account, "a.ofx", original)).Added.ShouldBe(2);

        // Same FITIDs; the bank corrected one amount and renamed the other payee. Neither is inserted.
        var corrected = Ofx(("F1", "20260805", "-13.50", "CORNER STORE"), ("F2", "20260806", "-40.00", "SHELL GAS STATION"));
        var parsed = await _host.ParseAsync("b.ofx", corrected);
        var preview = await Import.PreviewAsync(TransactionSource.File, Batch(account, parsed), Ct);
        preview.Rows.Select(r => r.Outcome).ShouldBe([DedupOutcome.UpdateInPlace, DedupOutcome.UpdateInPlace]);

        var summary = await _host.ImportFileAsync(account, "b.ofx", corrected);
        summary.Added.ShouldBe(0);
        summary.Updated.ShouldBe(2);
        var rows = await _host.ImportedAsync(account.Id);
        rows.Count.ShouldBe(2);
        rows.Single(r => r.ProviderTransactionId == "F1").Amount.ShouldBe(-1_350);
        rows.Single(r => r.ProviderTransactionId == "F2").PayeeRaw.ShouldBe("SHELL GAS STATION");

        // A third import of the corrected file is all duplicates.
        var third = await _host.ImportFileAsync(account, "b.ofx", corrected);
        (third.Added, third.Updated, third.DuplicatesSkipped).ShouldBe((0, 0, 2));
    }

    [Theory]
    [InlineData("signed", "Date,Description,Amount\n08/05/2026,GROCER,-25.10\n08/06/2026,REFUND SHOP,4.00\n")]
    [InlineData("debit-credit", "Date,Description,Debit,Credit\n08/05/2026,GROCER,25.10,\n08/06/2026,REFUND SHOP,,4.00\n")]
    [InlineData("amount-type", "Date,Description,Amount,Type\n08/05/2026,GROCER,25.10,Debit\n08/06/2026,REFUND SHOP,4.00,Credit\n")]
    public async Task Csv_amount_layouts_map_to_signed_amounts(string layout, string text)
    {
        var account = await _host.CheckingAsync();
        var parsed = await _host.ParseAsync(layout + ".csv", Utf8(text));
        parsed.CsvLayout!.Mapping.AmountLayout.ShouldBe(layout switch
        {
            "signed" => CsvAmountLayout.SignedAmount,
            "debit-credit" => CsvAmountLayout.DebitCredit,
            _ => CsvAmountLayout.AmountWithType,
        });

        (await Import.ImportTransactionsAsync(TransactionSource.File, Batch(account, parsed), Ct)).Added.ShouldBe(2);
        var rows = await _host.ImportedAsync(account.Id);
        rows.Select(r => (r.Date, r.Amount)).ShouldBe([(new DateOnly(2026, 8, 5), -2_510L), (new DateOnly(2026, 8, 6), 400L)]);
    }

    [Theory]
    [InlineData("2026-08-05|2026-08-17", "yyyy-MM-dd")]
    [InlineData("08/05/2026|08/17/2026", "MM/dd/yyyy")]
    [InlineData("05/08/2026|17/08/2026", "dd/MM/yyyy")]
    [InlineData("\"Aug 5, 2026\"|\"Aug 17, 2026\"", "MMM d, yyyy")]
    public async Task Date_formats_are_detected(string dates, string format)
    {
        var account = await _host.CheckingAsync();
        var d = dates.Split('|');
        var parsed = await _host.ParseAsync("dates.csv", Utf8($"Date,Description,Amount\n{d[0]},A,-1.00\n{d[1]},B,-2.00\n"));
        parsed.CsvLayout!.Mapping.DateFormat.ShouldBe(format);
        parsed.CsvLayout.IsDateFormatAmbiguous.ShouldBeFalse();

        await Import.ImportTransactionsAsync(TransactionSource.File, Batch(account, parsed), Ct);
        (await _host.ImportedAsync(account.Id)).Select(r => r.Date).ShouldBe([new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 17)]);
    }

    [Fact]
    public async Task Ambiguous_dates_are_reported_and_the_users_answer_is_imported()
    {
        var account = await _host.CheckingAsync();
        var csv = Utf8("Date,Description,Amount\n03/04/2026,A,-1.00\n05/06/2026,B,-2.00\n");
        var parsed = await _host.ParseAsync("ambiguous.csv", csv);
        parsed.CsvLayout!.IsDateFormatAmbiguous.ShouldBeTrue();
        parsed.CsvLayout.DateFormatCandidates.ShouldContain("dd/MM/yyyy");
        parsed.Warnings.ShouldContain(w => w.Code == ImportWarningCode.AmbiguousDateFormat);

        await _host.ImportFileAsync(account, "ambiguous.csv", csv, ImportOptions.Default with { PreferredDateOrder = DateOrder.DayFirst });
        (await _host.ImportedAsync(account.Id)).Select(r => r.Date).ShouldBe([new DateOnly(2026, 4, 3), new DateOnly(2026, 6, 5)]);
    }

    [Fact]
    public async Task A_manual_entry_is_matched_once_and_the_match_survives_reimport()
    {
        var account = await _host.CheckingAsync();
        var manual = await _host.AddAsync(account.Id, -4_250, "Trader Joe's", date: new DateOnly(2026, 8, 10));
        var csv = Utf8("Date,Description,Amount\n2026-08-11,TRADER JOE'S #552 PORTLAND,-42.50\n");

        var parsed = await _host.ParseAsync("tj.csv", csv);
        var preview = await Import.PreviewAsync(TransactionSource.File, Batch(account, parsed), Ct);
        preview.Rows.ShouldHaveSingleItem().Outcome.ShouldBe(DedupOutcome.MatchedToExisting);
        preview.Rows[0].ExistingTransactionId.ShouldBe(manual.Id);

        var summary = await _host.ImportFileAsync(account, "tj.csv", csv);
        (summary.Added, summary.MatchedToExisting).ShouldBe((0, 1));
        await using (var db = _host.Db())
        {
            var row = await db.Transactions.SingleAsync(t => t.Id == manual.Id);
            row.HasImportMatch.ShouldBeTrue();
            row.ImportFingerprint.ShouldNotBeNull();
            row.Status.ShouldBe(TransactionStatus.Cleared);
            row.Amount.ShouldBe(-4_250);
            row.Date.ShouldBe(new DateOnly(2026, 8, 10), "the user's entry keeps its own date");
        }

        (await _host.ImportFileAsync(account, "tj.csv", csv)).DuplicatesSkipped.ShouldBe(1);

        // A different purchase of the same amount a day later is not absorbed by the matched entry.
        var other = await _host.ImportFileAsync(account, "tj2.csv", Utf8("Date,Description,Amount\n2026-08-12,TRADER JOE'S #552,-42.50\n"));
        other.Added.ShouldBe(1);
        (await _host.CountAsync(account.Id)).ShouldBe(2);
    }

    [Fact]
    public async Task Transfers_pair_across_two_imports()
    {
        var checking = await _host.CheckingAsync();
        var savings = await _host.AccountAsync("Savings", AccountType.Savings);
        await _host.ImportFileAsync(checking, "c.csv", Utf8("Date,Description,Amount\n2026-08-05,ONLINE TRANSFER TO SAV 1234,-500.00\n2026-08-05,GROCER,-20.00\n"));

        var parsed = await _host.ParseAsync("s.csv", Utf8("Date,Description,Amount\n2026-08-07,TRANSFER FROM CHK 9876,500.00\n"));
        var preview = await Import.PreviewAsync(TransactionSource.File, Batch(savings, parsed), Ct);
        preview.Rows[0].TransferAccountId.ShouldBe(checking.Id);
        preview.Rows[0].PairTransferByDefault.ShouldBeTrue();

        var summary = await Import.ImportTransactionsAsync(TransactionSource.File, Batch(savings, parsed), Ct);
        (summary.Added, summary.TransfersMatched, summary.Uncategorized).ShouldBe((1, 1, 0));

        var outflow = (await _host.ImportedAsync(checking.Id)).Single(t => t.Amount == -50_000);
        var inflow = (await _host.ImportedAsync(savings.Id)).Single();
        inflow.TransferPairId.ShouldNotBeNull();
        outflow.TransferPairId.ShouldBe(inflow.TransferPairId);
        (outflow.TransferAccountId, inflow.TransferAccountId).ShouldBe((savings.Id, checking.Id));
        (outflow.PayeeId, inflow.PayeeId, outflow.CategoryId, inflow.CategoryId).ShouldBe((null, null, null, null));

        // The pair behaves like a transfer made by hand: deleting one side deletes both.
        await _host.Transactions.DeleteAsync([inflow.Id], Ct);
        (await _host.Transactions.GetAsync(outflow.Id, Ct))!.IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Transfer_to_a_tracking_account_keeps_the_category_on_the_budget_side()
    {
        var brokerage = await _host.AccountAsync("Brokerage", AccountType.Investment);
        var checking = await _host.CheckingAsync();
        var invest = await _host.CategoryAsync("Investing");
        await _host.ImportAsync(brokerage.Id, new IncomingTransaction(new DateOnly(2026, 8, 5), 30_000, "CONTRIBUTION"));
        await using (var db = _host.Db())
        {
            db.Payees.Add(new Payee { Name = "Broker", NormalizedName = "BROKER", DefaultCategoryId = invest });
            await db.SaveChangesAsync();
        }

        var summary = await _host.ImportAsync(checking.Id, new IncomingTransaction(new DateOnly(2026, 8, 4), -30_000, "BROKER"));
        (summary.TransfersMatched, summary.Uncategorized).ShouldBe((1, 0));
        (await _host.ImportedAsync(checking.Id)).Single().CategoryId.ShouldBe(invest);
        (await _host.ImportedAsync(brokerage.Id)).Single().CategoryId.ShouldBeNull();
    }

    [Fact]
    public async Task An_import_is_one_undoable_audited_action_with_one_message()
    {
        var checking = await _host.CheckingAsync();
        var savings = await _host.AccountAsync("Savings", AccountType.Savings);
        var manual = await _host.AddAsync(checking.Id, -1_999, "Hardware Store", date: new DateOnly(2026, 8, 1));
        await _host.ImportAsync(savings.Id, new IncomingTransaction(new DateOnly(2026, 8, 2), 10_000, "FROM CHECKING"));
        var messagesBefore = _host.Bus.LedgerChanges.Count;
        long auditBefore;
        await using (var db = _host.Db())
        {
            auditBefore = await db.AuditEvents.LongCountAsync();
        }

        var ofx = Ofx(("X1", "20260803", "-19.99", "HARDWARE STORE #12"), ("X2", "20260802", "-100.00", "TRANSFER TO SAVINGS"), ("X3", "20260809", "-7.00", "NEW CAFE"));
        var result = await _host.ParseAsync("x.ofx", ofx);
        var summary = await Import.ImportTransactionsAsync(TransactionSource.File, Batch(checking, result), Ct);
        (summary.Added, summary.MatchedToExisting, summary.TransfersMatched).ShouldBe((2, 1, 1));
        summary.ReportedBalance.ShouldNotBeNull();

        _host.Bus.LedgerChanges.Count.ShouldBe(messagesBefore + 1);
        _host.Undo.NextUndo.ShouldBe(LedgerAction.ImportTransactions);
        await using (var db = _host.Db())
        {
            // 2 inserts, 1 matched row, 1 transfer partner, 1 new payee, 1 snapshot, 1 account.
            (await db.AuditEvents.LongCountAsync() - auditBefore).ShouldBe(7);
        }

        (await _host.Undo.UndoAsync(Ct)).ShouldBe(LedgerAction.ImportTransactions);
        (await _host.ImportedAsync(checking.Id)).Select(t => t.Id).ShouldBe([manual.Id]);
        await using (var db = _host.Db())
        {
            var restored = await db.Transactions.SingleAsync(t => t.Id == manual.Id);
            (restored.HasImportMatch, restored.ImportFingerprint, restored.ProviderTransactionId, restored.Status)
                .ShouldBe((false, null, null, TransactionStatus.Uncleared));
            (await db.Transactions.SingleAsync(t => t.AccountId == savings.Id && t.Source == TransactionSource.File)).TransferPairId.ShouldBeNull();
            (await db.Payees.AnyAsync(p => p.Name == "New Cafe")).ShouldBeFalse();
            (await db.BalanceSnapshots.AnyAsync(s => s.AccountId == checking.Id)).ShouldBeFalse();
            (await db.Accounts.SingleAsync(a => a.Id == checking.Id)).ReportedBalance.ShouldBeNull();
        }

        await _host.Undo.RedoAsync(Ct);
        (await _host.CountAsync(checking.Id)).ShouldBe(3);
        (await Import.ImportTransactionsAsync(TransactionSource.File, Batch(checking, result), Ct)).Added.ShouldBe(0);
    }

    [Fact]
    public async Task Ofx_ledger_balance_is_recorded_as_a_reported_snapshot()
    {
        var account = await _host.CheckingAsync();
        await _host.ImportFileAsync(account, "bank-sgml.ofx", Fixture("bank-sgml.ofx"));

        var snapshots = await _host.Snapshots.GetSnapshotsAsync(account.Id, Ct);
        snapshots.ShouldHaveSingleItem().ShouldBe(new BalanceSnapshotDto(account.Id, new DateOnly(2026, 1, 31), 371_233, BalanceSource.Provider));
        (await _host.Register.GetSummaryAsync(account.Id, Ct)).ReportedBalance.ShouldBe(371_233);
    }

    [Fact]
    public async Task Preview_writes_nothing_and_agrees_with_the_import()
    {
        var account = await _host.CheckingAsync();
        await _host.ImportAsync(account.Id, new IncomingTransaction(new DateOnly(2026, 8, 1), -1_000, "OLD ROW"));
        var batch = new ImportBatch(account.Id,
        [
            new(new DateOnly(2026, 8, 1), -1_000, "OLD ROW"),
            new(new DateOnly(2026, 8, 2), -2_000, "NEW ROW"),
        ]);
        var messages = _host.Bus.LedgerChanges.Count;

        var preview = await Import.PreviewAsync(TransactionSource.File, batch, Ct);
        preview.Rows.Select(r => (r.Outcome, r.IncludeByDefault)).ShouldBe([(DedupOutcome.DuplicateSkipped, false), (DedupOutcome.Insert, true)]);
        preview.Rows[1].PayeeName.ShouldBe("New Row");
        _host.Bus.LedgerChanges.Count.ShouldBe(messages);
        (await _host.CountAsync(account.Id)).ShouldBe(1);

        var summary = await Import.ImportTransactionsAsync(TransactionSource.File, batch, Ct);
        (summary.Added, summary.DuplicatesSkipped).ShouldBe((preview.Count(DedupOutcome.Insert), preview.Count(DedupOutcome.DuplicateSkipped)));
    }

    [Fact]
    public async Task Overrides_skip_a_row_and_force_a_duplicate()
    {
        var account = await _host.CheckingAsync();
        await _host.ImportAsync(account.Id, new IncomingTransaction(new DateOnly(2026, 8, 1), -1_000, "COFFEE"));
        var batch = new ImportBatch(account.Id,
        [
            new(new DateOnly(2026, 8, 1), -1_000, "COFFEE"),
            new(new DateOnly(2026, 8, 2), -2_000, "UNWANTED"),
        ])
        {
            Overrides = new Dictionary<int, ImportRowOverride> { [0] = new(true), [1] = new(false) },
        };

        var summary = await Import.ImportTransactionsAsync(TransactionSource.File, batch, Ct);
        (summary.Added, summary.DuplicatesSkipped, summary.SkippedByChoice).ShouldBe((1, 0, 1));
        (await _host.ImportedAsync(account.Id)).Select(t => t.Amount).ShouldBe([-1_000L, -1_000L]);
    }

    [Fact]
    public async Task Payees_are_shared_and_default_categories_apply()
    {
        var account = await _host.CheckingAsync();
        var groceries = await _host.CategoryAsync("Groceries");
        var existing = await _host.Payees.GetOrCreateAsync("Trader Joes", Ct);
        await using (var db = _host.Db())
        {
            (await db.Payees.SingleAsync(p => p.Id == existing.Id)).DefaultCategoryId = groceries;
            await db.SaveChangesAsync();
        }

        var summary = await _host.ImportAsync(account.Id,
            new IncomingTransaction(new DateOnly(2026, 8, 1), -1_000, "TRADER JOES #123"),
            new IncomingTransaction(new DateOnly(2026, 8, 2), -2_000, "SQ *NEW BAKERY"),
            new IncomingTransaction(new DateOnly(2026, 8, 3), -3_000, "SQ *NEW BAKERY"));

        summary.Uncategorized.ShouldBe(2);
        var rows = await _host.ImportedAsync(account.Id);
        rows[0].PayeeId.ShouldBe(existing.Id);
        rows[0].CategoryId.ShouldBe(groceries);
        rows[1].PayeeId.ShouldNotBeNull();
        rows[2].PayeeId.ShouldBe(rows[1].PayeeId);
        (await _host.Transactions.GetAsync(rows[1].Id, Ct))!.Payee.ShouldBe("New Bakery");
    }

    [Fact]
    public async Task Categorization_hooks_run_in_order_and_can_rename_and_categorize()
    {
        var account = await _host.CheckingAsync();
        var dining = await _host.CategoryAsync("Dining");
        var hook = new RecordingHook(dining);
        var service = new ImportService(_host.Factory, _host.Get<LedgerWriter>(), [new NoOpImportCategorizationHook(), hook], NullLogger<ImportService>.Instance);

        var preview = await service.PreviewAsync(TransactionSource.File, new ImportBatch(account.Id, [new(new DateOnly(2026, 8, 1), -900, "TST* CORNER BISTRO")]), Ct);
        preview.Rows[0].PayeeName.ShouldBe("The Corner Bistro");
        preview.Rows[0].CategoryId.ShouldBe(dining);
        hook.Contexts.ShouldHaveSingleItem().IsPreview.ShouldBeTrue();

        await service.ImportTransactionsAsync(TransactionSource.File, new ImportBatch(account.Id, [new(new DateOnly(2026, 8, 1), -900, "TST* CORNER BISTRO")]), Ct);
        var row = (await _host.ImportedAsync(account.Id)).Single();
        row.CategoryId.ShouldBe(dining);
        row.IsApproved.ShouldBeTrue();
        (await _host.Transactions.GetAsync(row.Id, Ct))!.Payee.ShouldBe("The Corner Bistro");
    }

    [Fact]
    public async Task Pending_rows_are_replaced_by_their_posted_rows()
    {
        var account = await _host.CheckingAsync();
        var service = Import;
        await service.ImportTransactionsAsync(TransactionSource.Provider, new ImportBatch(account.Id,
            [new(new DateOnly(2026, 8, 1), -1_234, "COFFEE", ProviderTransactionId: "pend-1", IsPending: true)]), Ct);
        (await _host.ImportedAsync(account.Id)).Single().Status.ShouldBe(TransactionStatus.Uncleared);

        var summary = await service.ImportTransactionsAsync(TransactionSource.Provider, new ImportBatch(account.Id,
            [new(new DateOnly(2026, 8, 2), -1_500, "COFFEE", ProviderTransactionId: "post-1", ProviderPendingId: "pend-1")]), Ct);

        (summary.Added, summary.Updated).ShouldBe((0, 1));
        var row = (await _host.ImportedAsync(account.Id)).Single();
        (row.ProviderTransactionId, row.Amount, row.Date, row.Status).ShouldBe(("post-1", -1_500L, new DateOnly(2026, 8, 2), TransactionStatus.Cleared));
    }

    [Fact]
    public async Task Reconciled_rows_are_not_changed_by_an_update()
    {
        var account = await _host.CheckingAsync(opening: 0);
        await _host.ImportFileAsync(account, "a.ofx", Ofx(("R1", "20260805", "-10.00", "SHOP")));
        await _host.Transactions.FinishReconciliationAsync(new FinishReconciliationRequest(account.Id, new DateOnly(2026, 8, 31), -1_000, CreateAdjustment: false), Ct);

        var summary = await _host.ImportFileAsync(account, "b.ofx", Ofx(("R1", "20260805", "-11.00", "SHOP")));
        summary.Updated.ShouldBe(0);
        summary.Warnings.ShouldContain(w => w.Code == ImportWarningCode.ReconciledNotUpdated);
        (await _host.ImportedAsync(account.Id)).Single().Amount.ShouldBe(-1_000);
    }

    [Fact]
    public async Task Import_into_a_closed_or_missing_account_is_refused()
    {
        var ex = await Should.ThrowAsync<LedgerValidationException>(() => _host.ImportAsync(Guid.NewGuid(), new IncomingTransaction(new DateOnly(2026, 8, 1), -1, "X")));
        ex.Error.ShouldBe(LedgerError.AccountNotFound);

        var account = await _host.CheckingAsync(opening: 0);
        await _host.Accounts.CloseAccountAsync(account.Id, closeWithBalance: false, Ct);
        (await Should.ThrowAsync<LedgerValidationException>(() => _host.ImportAsync(account.Id, new IncomingTransaction(new DateOnly(2026, 8, 1), -1, "X"))))
            .Error.ShouldBe(LedgerError.AccountClosed);
    }

    [Fact]
    public async Task An_all_duplicate_import_changes_nothing_and_is_not_on_the_undo_stack()
    {
        var account = await _host.CheckingAsync();
        await _host.ImportAsync(account.Id, new IncomingTransaction(new DateOnly(2026, 8, 1), -1_000, "A"));
        var undoDepth = _host.Undo.NextUndo;
        var messages = _host.Bus.LedgerChanges.Count;

        var summary = await _host.ImportAsync(account.Id, new IncomingTransaction(new DateOnly(2026, 8, 1), -1_000, "A"));

        summary.HasChanges.ShouldBeFalse();
        _host.Bus.LedgerChanges.Count.ShouldBe(messages);
        _host.Undo.NextUndo.ShouldBe(undoDepth);
    }

    [Fact]
    public async Task Bulk_inserted_rows_are_stored_exactly_like_rows_saved_through_ef()
    {
        var account = await _host.CheckingAsync(opening: 0);
        var manual = await _host.AddAsync(account.Id, -700, "Hand Entry", date: new DateOnly(2026, 8, 20));
        await _host.ImportAsync(account.Id, new IncomingTransaction(new DateOnly(2026, 8, 21), -1_234, "SQ *CAFE", "note", "FIT-9"));
        var imported = (await _host.ImportedAsync(account.Id)).Single(t => t.Source == TransactionSource.File);

        await using (var db = _host.Db())
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "Id", "AccountId", "Date", "Status", "Source", typeof("Amount"), typeof("IsApproved"), "HasImportMatch", "CreatedAt"
                FROM "Transactions" WHERE "Id" IN (@a, @b) ORDER BY "Date"
                """;
            command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@a", manual.Id.ToString("D").ToUpperInvariant()));
            command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@b", imported.Id.ToString("D").ToUpperInvariant()));
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string[]>();
            while (await reader.ReadAsync())
            {
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!).ToArray());
            }

            rows.Count.ShouldBe(2, "both ids are stored as upper-case text");
            rows[0][2].ShouldBe("2026-08-20");
            rows[1][2].ShouldBe("2026-08-21");
            (rows[0][3], rows[1][3], rows[0][4], rows[1][4]).ShouldBe(("Uncleared", "Cleared", "Manual", "File"));
            rows[1][5].ShouldBe(rows[0][5]);
            rows[1][6].ShouldBe(rows[0][6]);
            rows[1][7].ShouldBe("0");
            rows[1][8].Length.ShouldBe(rows[0][8].Length);
        }

        // The register pages, searches and balances imported rows like any other.
        var page = await _host.Register.GetPageAsync(new RegisterFilter(account.Id), RegisterSort.Default, 0, 10, Ct);
        page.Select(r => (r.Payee, r.Amount, r.RunningBalance)).ShouldBe([("Cafe", -1_234L, -1_934L), ("Hand Entry", -700L, -700L)]);
        (await _host.Register.CountAsync(new RegisterFilter(account.Id, Search: "cafe"), Ct)).ShouldBe(1);
        (await _host.Transactions.GetAsync(imported.Id, Ct))!.Memo.ShouldBe("note");
    }

    private static byte[] Ofx(params (string Fitid, string Date, string Amount, string Name)[] rows)
    {
        var body = string.Concat(rows.Select(r =>
            $"<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>{r.Date}<TRNAMT>{r.Amount}<FITID>{r.Fitid}<NAME>{r.Name}</STMTTRN>\n"));
        return Utf8($"""
            OFXHEADER:100
            DATA:OFXSGML
            VERSION:102

            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>USD
            <BANKACCTFROM><BANKID>123<ACCTID>999<ACCTTYPE>CHECKING</BANKACCTFROM>
            <BANKTRANLIST><DTSTART>20260801<DTEND>20260831
            {body}</BANKTRANLIST>
            <LEDGERBAL><BALAMT>1234.56<DTASOF>20260831</LEDGERBAL>
            </STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """);
    }

    private sealed class RecordingHook(Guid category) : IImportCategorizationHook
    {
        public List<ImportHookContext> Contexts { get; } = [];

        public ValueTask RenamePayeesAsync(ImportHookContext context, IReadOnlyList<ImportDraft> drafts, CancellationToken ct)
        {
            Contexts.Add(context);
            foreach (var d in drafts.Where(d => d.NormalizedPayee == "CORNER BISTRO"))
            {
                d.PayeeName = "The Corner Bistro";
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask CategorizeAsync(ImportHookContext context, IReadOnlyList<ImportDraft> drafts, CancellationToken ct)
        {
            foreach (var d in drafts)
            {
                d.CategoryId ??= category;
                d.IsApproved = true;
            }

            return ValueTask.CompletedTask;
        }
    }
}
