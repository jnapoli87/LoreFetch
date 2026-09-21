using Avalonia;
using Avalonia.Markup.Xaml;

namespace LoreFetch.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}
