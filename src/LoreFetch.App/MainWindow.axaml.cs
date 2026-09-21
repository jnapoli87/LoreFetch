using Avalonia.Controls;

namespace LoreFetch.App;

/// The app's single window for A1: a status line sourced from
/// `IScanPipeline.SourceDescription` (never composed by hand — CONTRACTS.md
/// "Composition") and a placeholder where the preview, overlay and cohort
/// grid land in A2 through A5. No buttons exist yet, so the "never set
/// IsDefault on any button" global A override has nothing to violate here —
/// it becomes relevant once A6 adds interactive controls.
public partial class MainWindow : Window
{
    // Parameterless constructor required by the Avalonia XAML previewer and
    // the compiled-XAML loader.
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(AppSession session)
        : this()
    {
        ArgumentNullException.ThrowIfNull(session);

        // The ONE place SourceDescription is read — never composed from
        // parts, per the global A override.
        StatusText.Text = session.Pipeline.SourceDescription;
    }
}
