using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace LoreFetch.App;

/// <summary>
/// File → Rename run… (issue #20): asks for the run's new name. Closes with
/// the text typed, or null on Cancel. Validation stays in
/// <see cref="RunStore.Rename"/>, which explains a bad name through the
/// window's notice banner, so this dialog holds no rules of its own.
/// </summary>
internal sealed class RenameRunDialog : Window
{
    public RenameRunDialog(string currentName)
    {
        Title = "Rename run";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var name = new TextBox { Text = currentName };

        // IsDefault / IsCancel are safe here: this is its own window, so they
        // never meet MainWindow's Enter/Escape tunnel handler.
        var rename = new Button { Content = "Rename", IsDefault = true };
        rename.Click += (_, _) => Close(name.Text ?? string.Empty);

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Run name" },
                name,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, rename },
                },
            },
        };

        Opened += (_, _) =>
        {
            name.Focus();
            name.SelectAll();
        };
    }
}
