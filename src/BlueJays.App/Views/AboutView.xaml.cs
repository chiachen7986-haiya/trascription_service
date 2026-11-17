using System.Reflection;
using System.Windows.Controls;

namespace BlueJays.App.Views;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        var version = typeof(AboutView).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText.Text = "Version " + version?.Split('+')[0];
    }
}
