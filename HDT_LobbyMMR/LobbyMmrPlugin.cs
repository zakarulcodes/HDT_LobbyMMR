using System;
using System.Windows.Controls;
using Hearthstone_Deck_Tracker.Plugins;

namespace HDT_LobbyMMR
{
    /// <summary>
    /// HDT plugin entry point. Shows the MMR of every player in the current
    /// Battlegrounds lobby in a panel skinned to match HDT's Battlegrounds
    /// Session window, docked to the top of that window.
    /// </summary>
    public class LobbyMmrPlugin : IPlugin
    {
        public MenuItem MenuItem { get; private set; }

        private LobbyMmr _engine;
        private DateTime _lastUpdate = DateTime.Now;

        public string Name => "HDT_LobbyMMR";
        public string Description => "Shows the MMR of every player in the Battlegrounds lobby, docked to the top of the Battlegrounds Session window.";
        public string Author => "Zakarul";
        public Version Version => new Version(1, 0, 0);
        public string ButtonText => "Reset";

        public void OnLoad()
        {
            FileLogger.Initialize(LogLevel.Info, 5);
            CreateMenuItem();
            MenuItem.IsChecked = true;
        }

        public void OnUnload()
        {
            MenuItem.IsChecked = false;
            FileLogger.Instance.Clean();
        }

        public void OnButtonPress()
        {
            // Re-create the panel from scratch (useful if the overlay was rebuilt).
            if (MenuItem.IsChecked)
            {
                RemoveComponents();
                CreateComponents();
            }
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
            MenuItem = new MenuItem
            {
                Header = "Lobby MMR",
                IsCheckable = true
            };
            MenuItem.Checked += (sender, args) => CreateComponents();
            MenuItem.Unchecked += (sender, args) => RemoveComponents();
        }

        private void CreateComponents()
        {
            _engine ??= new LobbyMmr();
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
