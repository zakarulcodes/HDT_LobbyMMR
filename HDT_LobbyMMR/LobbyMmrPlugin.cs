using System;
using System.IO;
using System.Windows.Controls;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Plugins;

namespace HDT_LobbyMMR
{
    /// <summary>
    /// HDT plugin entry point. Shows the MMR of every player in the current
    /// Battlegrounds lobby in a panel skinned to match HDT's Battlegrounds
    /// Session window, docked to the top or bottom of that window.
    /// </summary>
    public class LobbyMmrPlugin : IPlugin
    {
        public MenuItem MenuItem { get; private set; }

        private LobbyMmr _engine;
        private DateTime _lastUpdate = DateTime.Now;
        private DockSide _dockSide = DockSide.Top;
        private MenuItem _dockTopItem;
        private MenuItem _dockBottomItem;

        public string Name => "HDT_LobbyMMR";
        public string Description => "Shows the MMR of every player in the Battlegrounds lobby, docked to the top or bottom of the Battlegrounds Session window.";
        public string Author => "Zakarul";
        public Version Version => new Version(1, 0, 0);
        public string ButtonText => "Toggle dock: top / bottom";

        public void OnLoad()
        {
            FileLogger.Initialize(LogLevel.Info, 5);
            CreateMenuItem();
            CreateComponents();
        }

        public void OnUnload()
        {
            RemoveComponents();
            FileLogger.Instance.Clean();
        }

        public void OnButtonPress()
        {
            // The button (Options -> Tracker -> Plugins) flips the dock side; it is
            // the most discoverable control and mirrors the submenu selection.
            SetDockSide(_dockSide == DockSide.Top ? DockSide.Bottom : DockSide.Top);
        }

        public void OnUpdate()
        {
            if (_engine == null)
                return;

            // HDT calls OnUpdate roughly every 100ms; throttle the heavier work to ~1s.
            if ((DateTime.Now - _lastUpdate).TotalSeconds >= 1)
            {
                _lastUpdate = DateTime.Now;
                try
                {
                    _engine.OnUpdate();
                }
                catch (Exception ex)
                {
                    FileLogger.Instance.Error("engine.OnUpdate threw", ex);
                }
            }
        }

        private void CreateMenuItem()
        {
            _dockSide = LoadDockSide();

            // A plain container (NOT checkable) so its submenu expands normally.
            // A checkable parent that also has children renders as a checkbox and
            // hides the submenu, which is why the dock options were not showing.
            MenuItem = new MenuItem
            {
                Header = "Lobby MMR"
            };

            // Mutually-exclusive dock-side options shown as a submenu.
            _dockTopItem = new MenuItem
            {
                Header = "Dock to top",
                IsCheckable = true,
                IsChecked = _dockSide == DockSide.Top
            };
            _dockBottomItem = new MenuItem
            {
                Header = "Dock to bottom",
                IsCheckable = true,
                IsChecked = _dockSide == DockSide.Bottom
            };
            _dockTopItem.Click += (sender, args) => SetDockSide(DockSide.Top);
            _dockBottomItem.Click += (sender, args) => SetDockSide(DockSide.Bottom);
            MenuItem.Items.Add(_dockTopItem);
            MenuItem.Items.Add(_dockBottomItem);
        }

        private void SetDockSide(DockSide side)
        {
            _dockSide = side;
            // Keep the two options mutually exclusive (clicking toggled the source item).
            _dockTopItem.IsChecked = side == DockSide.Top;
            _dockBottomItem.IsChecked = side == DockSide.Bottom;
            SaveDockSide(side);
            _engine?.SetDockSide(side);
        }

        private void CreateComponents()
        {
            _engine ??= new LobbyMmr();
            _engine.SetDockSide(_dockSide);
        }

        // ---- Dock-side preference persistence -------------------------------

        private static string DockSidePath =>
            Path.Combine(Config.AppDataPath, "LobbyMMR", "dockside.txt");

        private static DockSide LoadDockSide()
        {
            try
            {
                string path = DockSidePath;
                if (File.Exists(path) &&
                    File.ReadAllText(path).Trim().Equals("bottom", StringComparison.OrdinalIgnoreCase))
                    return DockSide.Bottom;
            }
            catch (Exception ex)
            {
                FileLogger.Instance.Error("Failed to load dock side", ex);
            }
            return DockSide.Top;
        }

        private static void SaveDockSide(DockSide side)
        {
            try
            {
                string path = DockSidePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, side == DockSide.Bottom ? "bottom" : "top");
            }
            catch (Exception ex)
            {
                FileLogger.Instance.Error("Failed to save dock side", ex);
            }
        }

        private void RemoveComponents()
        {
            if (_engine != null)
            {
                _engine.Clean();
                _engine = null;
            }
        }
    }
}
