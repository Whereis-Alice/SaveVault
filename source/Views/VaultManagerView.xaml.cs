using System.Windows.Controls;

namespace SaveVault.Views
{
    /// <summary>
    /// Host for the manager window's content. A plain <see cref="UserControl" /> rather than a
    /// PluginUserControl: it is shown in a window created through IDialogsFactory.CreateWindow,
    /// where there is no game context for Playnite to push into it.
    ///
    /// The view is deliberately code free. Everything it needs - selection, filtering, the quota
    /// bar - lives on VaultManagerViewModel, which is also what the panel's "manage" link and the
    /// main menu entry share.
    /// </summary>
    public partial class VaultManagerView : UserControl
    {
        public VaultManagerView()
        {
            InitializeComponent();
        }
    }
}
