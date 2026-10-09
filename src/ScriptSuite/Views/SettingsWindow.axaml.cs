using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScriptSuite.Controls;
using ScriptSuite.Models;
using ScriptSuite.Services;
using ScriptSuite.ViewModels;

namespace ScriptSuite.Views;

/// <summary>Schema-driven settings screen for one script. Hosts the reusable
/// SettingsForm control; flushes any in-flight text edits when the window
/// closes so nothing typed is ever lost. Linux counterpart of the Windows
/// SettingsWindow — same structure, XDG Trash / journald / freedesktop
/// wording in the overviews instead of Recycle Bin / .evtx.</summary>
public partial class SettingsWindow : Window
{
    private SettingsFormViewModel? _form;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(ScriptManifest manifest, ScriptConfigService config)
        : this()
    {
        Title = "Settings — " + manifest.DisplayName;
        ScriptNameText.Text = manifest.DisplayName;
        ScriptDescriptionText.Text = manifest.Description;

        _form = new SettingsFormViewModel(manifest, config);
        SettingsFormControl.Form = _form;

        if (manifest.Fields.Count == 0)
        {
            SettingsFormControl.IsVisible = false;
            NoFieldsText.IsVisible = true;
        }

        // Advanced per-extension rules — DownloadsCleanup only, additive to simple mode.
        if (string.Equals(manifest.Id, "DownloadsCleanup", StringComparison.OrdinalIgnoreCase))
        {
            AdvancedRulesControl.DataContext = new DownloadsAdvancedRulesViewModel(config);
            AdvancedRulesControl.IsVisible = true;
        }

        // Script-level help (window header ?)
        var overview = GetScriptOverviewHelp(manifest.Id);
        if (!string.IsNullOrWhiteSpace(overview))
        {
            HelpTextBlock.Text = overview;
            HelpButton.IsVisible = true;
        }
    }

    private static string GetScriptOverviewHelp(string id) => id switch
    {
        "TempCleanup" => "Deletes old files from the temp folder permanently (rm, not the Trash — temp files are expendable by design). Only files older than the cutoff are deleted — recent temp files are left alone. Folders are never deleted.",
        "DownloadsCleanup" => "Sorts old files in your Downloads folder. Files older than the cutoff and matching the delete list go to the XDG Trash; other old files are moved into category folders. Advanced Rules below always win over the simple lists for the same extension. Ignore means never touch, even if old. MoveTo needs a valid folder — if empty or missing, that rule is skipped and logged.",
        "ScreenshotsCleanup" => "Deletes old screenshots to the XDG Trash so you can restore them. Only files directly in the target folder are checked. If the folder does not exist yet, nothing happens.",
        "EmptyFolderCleanup" => "Removes empty folders under the chosen root. It checks recursively and makes multiple passes, but never deletes the root folder itself. Only empty folders are removed, so no data is lost — they are removed directly (rmdir), not trashed, because they contain nothing. Folders on the ignore list (with everything under them) are never touched.",
        "EmptyTrash" => "Permanently empties the XDG Trash. Files are not moved to another folder — they are deleted and cannot be recovered. The preview shows what would be deleted. Use the age filter to keep recently trashed items, or 0 to empty everything.",
        "ClearJournal" => "Vacuums the systemd journal after saving an export snapshot. Each in-scope boot's entries are exported to a file in the backup folder first, then the journal is vacuumed by time and size. If an export fails, nothing is vacuumed. Requires admin (pkexec) and therefore cannot run on a timer.",
        "SoftwareInventory" => "Writes a plain-text inventory of installed packages (dpkg, snap, flatpak) to the output file. Changes nothing — report only, so there is no dry-run preview.",
        "SystemHealthReport" => "Writes an HTML system health report (disk, memory, top processes, recent errors) to the output folder. Changes nothing — report only, so there is no dry-run preview.",
        _ => ""
    };

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _form?.FlushPending();
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void HelpButton_Click(object? sender, RoutedEventArgs e)
    {
        HelpPanel.IsVisible = !HelpPanel.IsVisible;
    }
}
