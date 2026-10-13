using CommunityToolkit.Mvvm.ComponentModel;
using ScriptSuite.Models;

namespace ScriptSuite.ViewModels;

/// <summary>One dry-run preview row with a user-toggleable checkbox.
/// Checked by default; unchecked Targets are excluded from the real run via
/// --include-only-file. Skip rows are filtered out before Confirm (they must
/// never reach the include file); Selectable=false renders informational
/// rows for global-effect scripts. Ported from windows-scripts
/// (CommunityToolkit style).</summary>
public partial class RunSelectionItem : ViewModelBase
{
    public DryRunItem Item { get; }

    public string ActionDisplay => Item.ActionDisplay;
    public string Target => Item.Target;
    public string Detail => Item.Detail;

    /// <summary>False for global-effect scripts: row renders as information,
    /// no checkbox. Set by the owning VM from the manifest.</summary>
    public bool Selectable { get; set; } = true;

    [ObservableProperty] private bool _isChecked = true;

    public RunSelectionItem(DryRunItem item) => Item = item;
}
