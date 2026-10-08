using System.Reflection;
using Avalonia.Controls;

namespace Neruna.Desktop.Views;

/// <summary>Settings → Info: version, licence (MPL 2.0), third-party components, trademark.</summary>
internal sealed partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        var version = typeof(AboutView).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        this.FindControl<TextBlock>("VersionText")!.Text = $"Version {version.Split('+')[0]} (Beta)";
    }
}
