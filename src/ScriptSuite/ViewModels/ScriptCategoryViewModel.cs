using System.Collections.ObjectModel;

namespace ScriptSuite.ViewModels;

/// <summary>One dashboard section: all cards of a manifest category
/// (Cleanup / System / Reports), in catalog order. Ported from
/// windows-scripts ScriptCategoryViewModel.</summary>
public sealed class ScriptCategoryViewModel : ViewModelBase
{
    public string Name { get; }
    public ObservableCollection<ScriptCardViewModel> Rows { get; } = new();

    public ScriptCategoryViewModel(string category)
    {
        Name = category;
    }
}
