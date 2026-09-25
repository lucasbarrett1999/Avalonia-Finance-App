using Keel.Application.Accounts;
using Keel.Application.Attachments;
using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Application.Rules;
using Keel.Desktop.Services;
using Keel.Domain;
using Keel.Domain.Rules;

namespace Keel.Desktop.Tests;

/// <summary>The file picker and launcher of attachments, faked: picks what the test says and records what was opened.</summary>
internal sealed class FakeAttachmentFiles : IAttachmentFiles
{
    public List<string> ToPick { get; } = [];

    public List<string> Opened { get; } = [];

    public Task<IReadOnlyList<string>> PickAsync() => Task.FromResult<IReadOnlyList<string>>([.. ToPick]);

    public Task<bool> OpenAsync(string path)
    {
        Opened.Add(path);
        return Task.FromResult(true);
    }
}

/// <summary>
/// A small ledger for the M9c screens (F-TXN-8, F-TXN-9): tagged transactions (with the rules' Flagged tag), one
/// attachment, a rule that names a tag, and three spellings of one payee to merge.
/// </summary>
internal sealed record TagTestLedger(Guid Checking, Guid Hotel, Guid Airline, Guid Grocer, Guid Groceries, string ReceiptFolder)
{
    private static readonly DateOnly Day = DateOnly.FromDateTime(DateTime.Today).AddDays(-3);

    public static async Task<TagTestLedger> CreateAsync(TestHost host)
    {
        var ct = CancellationToken.None;
        return await Task.Run(async () =>
        {
            var accounts = host.Current<IAccountService>();
            var transactions = host.Current<ITransactionService>();
            var checking = await accounts.CreateAccountAsync(new CreateAccountRequest("Checking", AccountType.Checking, "USD", Day.AddDays(-30), 250_000), ct);
            var groceries = (await host.Current<ICategoryService>().CreateCategoryAsync("Everyday", "Groceries", ct)).Id;
            var travel = (await host.Current<ICategoryService>().CreateCategoryAsync("Everyday", "Travel", ct)).Id;

            async Task<Guid> AddAsync(long amount, string payee, Guid? category, string? memo, params string[] tags) =>
                (await transactions.SaveAsync(new SaveTransactionRequest(null, checking.Id, Day, amount, payee, category, memo, Tags: tags), ct)).Id;

            var hotel = await AddAsync(-48_900, "Harbor Hotel", travel, "two nights", "Trip 2026", "Work");
            var airline = await AddAsync(-31_250, "Blue Air", travel, null, "Trip 2026");
            var grocer = await AddAsync(-6_420, "Corner Grocer", groceries, "weekly shop");
            await AddAsync(-1_999, "Amazon", null, null, "Flagged");
            await AddAsync(-2_450, "AMZN Mktp", groceries, null);
            await AddAsync(-3_300, "Amazon.com", null, "cables");

            await host.Current<IRuleService>().SaveAsync(new RuleDefinition
            {
                Name = "Hotels are trips",
                Conditions = new RuleConditionSet { Conditions = [new PayeeCondition(TextOperator.Contains, "hotel")] },
                Actions = new RuleActionSet { Actions = [new AddTagAction("Trip 2026")] },
            }, ct);

            var folder = Path.Combine(host.Root, "receipts");
            Directory.CreateDirectory(folder);
            var receipt = Path.Combine(folder, "hotel-receipt.pdf");
            await System.IO.File.WriteAllTextAsync(receipt, "%PDF-1.4 hotel receipt", ct);
            await host.Current<IAttachmentService>().AddAsync(hotel, receipt, ct);
            return new TagTestLedger(checking.Id, hotel, airline, grocer, groceries, folder);
        });
    }

    /// <summary>Writes a file to attach and returns its path.</summary>
    public string File(string name, string content)
    {
        var path = Path.Combine(ReceiptFolder, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }
}
