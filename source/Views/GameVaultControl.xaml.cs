using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using SaveVault.ViewModels;

namespace SaveVault.Views
{
    /// <summary>
    /// The panel a theme mounts into the details view.
    ///
    /// The control never builds its own view model: the plugin owns exactly one
    /// <see cref="GameVaultViewModel" /> and hands it over as the DataContext, so a theme that
    /// mounts the panel in both the details view and the grid details pane shows one consistent
    /// state instead of two that drift apart.
    /// </summary>
    public partial class GameVaultControl : PluginUserControl
    {
        public GameVaultControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Playnite raises this on the mounted control while the user arrow-keys through a list.
        /// SetGame only rebuilds a projection of the stored profile - no disk scan, no hashing - so
        /// it is cheap enough to run on every keystroke.
        /// </summary>
        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            var model = DataContext as GameVaultViewModel;
            if (model != null)
            {
                model.SetGame(newContext);
            }
        }
    }
}
