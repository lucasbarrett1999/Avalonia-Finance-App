using Keel.Application.Accounts;
using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Application.Rules;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Register;
using Keel.Domain;
using Keel.Domain.Rules;

namespace Keel.Desktop.ViewModels.Rules;

/// <summary>
/// Opens the rule dialogs from anywhere (rules list, review queue, register context menu): new,
/// edit, "Create rule from this transaction", delete with confirmation, and apply to existing
/// transactions with a preview. Reports results in the status strip with Undo.
/// </summary>
public sealed class RuleEditorFlow(IRuleService rules, ICategoryService categories, IAccountService accounts, DialogService dialogs, StatusService status)
{
    /// <summary>The dialog service (tests read the open dialog).</summary>
    public DialogService Dialogs => dialogs;

    /// <summary>Loads what the editor refers to.</summary>
    public async Task<RuleEditorContext> ContextAsync()
    {
        var categoryList = await categories.GetCategoriesAsync(includeHidden: true, CancellationToken.None);
        var accountList = await accounts.GetAccountsAsync(includeClosed: true, CancellationToken.None);
        var options = categoryList.Where(c => !c.IsCreditCardPayment).Select(CategoryOption.From).ToList();
        var accountOptions = accountList.Select(AccountOption.From).ToList();
        var validation = new RuleValidationContext(categoryList.Select(c => c.Id).ToHashSet(), accountList.Select(a => a.Id).ToHashSet());
        var currency = accountList.FirstOrDefault()?.Balance.Currency ?? Currency.Default;
        return new RuleEditorContext(options, accountOptions, currency, validation);
    }

    /// <summary>Creates the editor dialog (not shown).</summary>
    public async Task<RuleEditorViewModel> CreateEditorAsync(RuleDefinition rule) =>
        new(rules, await ContextAsync(), rule);

    /// <summary>Opens the editor for a new rule.</summary>
    public async Task<RuleDto?> NewAsync(RuleDefinition? prefill = null)
    {
        var rule = prefill ?? new RuleDefinition
        {
            Name = string.Empty,
            Conditions = new RuleConditionSet { Conditions = [new PayeeCondition(TextOperator.Contains, string.Empty)] },
            Actions = new RuleActionSet { Actions = [new SetCategoryAction(Guid.Empty)] },
        };
        return await ShowEditorAsync(await CreateEditorAsync(rule));
    }

    /// <summary>"Create rule from this transaction": the editor prefilled by <see cref="RuleSuggester"/>.</summary>
    public async Task<RuleDto?> CreateFromTransactionAsync(Guid transactionId)
    {
        RuleDefinition suggested;
        try
        {
            suggested = await rules.SuggestFromTransactionAsync(transactionId, CancellationToken.None);
        }
        catch (LedgerValidationException ex)
        {
            status.Show(LedgerText.Error(ex.Error), isError: true);
            return null;
        }

        return await ShowEditorAsync(await CreateEditorAsync(suggested));
    }

    /// <summary>Opens the editor for an existing rule.</summary>
    public async Task<RuleDto?> EditAsync(RuleDto rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Definition is null)
        {
            status.Show(LedgerText.Format(Strings.Rules_Unreadable, rule.FormatError), isError: true);
            return null;
        }

        return await ShowEditorAsync(await CreateEditorAsync(rule.Definition));
    }

    /// <summary>Shows an editor; after saving, optionally previews applying the rule to existing transactions.</summary>
    public async Task<RuleDto?> ShowEditorAsync(RuleEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (!await dialogs.ShowAsync(editor) || editor.Saved is not { } saved)
        {
            return null;
        }

        status.Show(LedgerText.Format(Strings.Status_RuleSaved, saved.Name), offerUndo: true);
        if (editor.ApplyToExisting)
        {
            await ApplyToExistingAsync([saved.Id], LedgerText.Format(Strings.Retroactive_OneRule, saved.Name));
        }

        return saved;
    }

    /// <summary>Previews and (on confirmation) applies rules to existing transactions.</summary>
    public async Task<RetroactiveResult?> ApplyToExistingAsync(IReadOnlyList<Guid> ruleIds, string label)
    {
        var dialog = new RetroactiveApplyViewModel(rules, ruleIds, label);
        if (!await dialogs.ShowAsync(dialog) || dialog.Result is not { } result)
        {
            return null;
        }

        status.Show(LedgerText.Format(Strings.Status_RulesApplied, result.Changed), offerUndo: result.Changed > 0);
        return result;
    }

    /// <summary>Deletes a rule after confirmation.</summary>
    public async Task<bool> DeleteAsync(RuleDto rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var confirm = new ConfirmDialogViewModel(Strings.Rules_DeleteTitle, LedgerText.Format(Strings.Rules_DeleteMessage, rule.Name), Strings.Rules_Delete);
        if (!await dialogs.ShowAsync(confirm))
        {
            return false;
        }

        try
        {
            await rules.DeleteAsync(rule.Id, CancellationToken.None);
            status.Show(LedgerText.Format(Strings.Status_RuleDeleted, rule.Name), offerUndo: true);
            return true;
        }
        catch (LedgerValidationException ex)
        {
            status.Show(LedgerText.Error(ex.Error), isError: true);
            return false;
        }
    }
}
