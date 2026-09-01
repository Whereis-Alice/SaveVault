using System.Windows.Controls;

namespace SaveVault.Views
{
    /// <summary>
    /// Host for the add-on settings page. A plain <see cref="UserControl" /> rather than a
    /// PluginUserControl: Playnite shows this inside its own settings window, where there is no
    /// game context to track.
    /// </summary>
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
        }
    }
}
