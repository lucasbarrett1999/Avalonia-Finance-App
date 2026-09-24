using CommunityToolkit.Mvvm.Input;
using Keel.Desktop.ViewModels.Rules;

namespace Keel.Desktop.ViewModels;

/// <summary>The register's "Create rule from this transaction" (F-TXN-4) context-menu entry.</summary>
public sealed partial class AccountsViewModel
{
    private readonly RuleEditorFlow _ruleEditor;

    /// <summary>Opens the rule editor prefilled from the selected transaction.</summary>
    [RelayCommand]
    public async Task CreateRuleFromTransactionAsync()
    {
        if (Selection.Count == 1 && Selection[0].IsLoaded)
        {
            await _ruleEditor.CreateFromTransactionAsync(Selection[0].Id);
        }
    }
}
