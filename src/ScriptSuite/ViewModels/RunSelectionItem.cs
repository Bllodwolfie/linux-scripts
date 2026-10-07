using CommunityToolkit.Mvvm.ComponentModel;
using ScriptSuite.Models;

namespace ScriptSuite.ViewModels;

/// <summary>One dry-run preview row with a user-toggleable checkbox.
/// Checked by default; unchecked Targets are excluded from the real run via
/// --include-only-file. Ported from windows-scripts (CommunityToolkit style).</summary>
public partial class RunSelectionItem : ViewModelBase
{
    public DryRunItem Item { get; }

    public string ActionDisplay => Item.ActionDisplay;
    public string Target => Item.Target;
    public string Detail => Item.Detail;

    [ObservableProperty] private bool _isChecked = true;

    public RunSelectionItem(DryRunItem item) => Item = item;
}
