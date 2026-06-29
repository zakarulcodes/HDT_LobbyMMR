using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Enums;

namespace HDT_LobbyMMR
{
    /// <summary>Which edge of the Battlegrounds Session window the panel docks to.</summary>
    public enum DockSide { Top, Bottom }

    /// <summary>
    /// Core logic: reads the Battlegrounds lobby players, resolves their MMR from
    /// the BGrank leaderboard service, and renders them in a panel docked to the top
    /// or bottom of HDT's Battlegrounds Session window.
    ///
    /// The leaderboard data flow is adapted from IBM5100's HDT_BGrank
    /// (https://github.com/IBM5100o/HDT_BGrank).
    /// </summary>
    public class LobbyMmr
    {
        private bool _isReset = true;
        private bool _done = false;
        private bool _failToGetData = false;
        private bool _leaderBoardReady = false;
        private int _nameErrors = 0;
        private bool _docked = false;
        private DockSide _dockSide = DockSide.Top;
        private DockSide? _dockedAt = null;

        private string _myName;
        private List<string> _lobbyNames;
        private Dictionary<string, string> _leaderBoard;

        private Mirror _mirror;
        private HttpClient _client;
        private LobbyMmrPanel _panel;

        public LobbyMmr()
        {
            _client = new HttpClient();
            _client.DefaultRequestHeaders.Add("User-Agent", "HDT_LobbyMMR");
            _client.Timeout = TimeSpan.FromSeconds(15);
            _mirror = new Mirror();
            _panel = new LobbyMmrPanel();
        }

        public void Clean()
        {
            Undock();
            ClearMemory();
            _client?.Dispose();
            _client = null;
            _mirror = null;
            _panel = null;
        }

        // ---- Docking to the Battlegrounds Session window --------------------

        /// <summary>
        /// Choose which edge of the session window the panel docks to. The move is
        /// applied on the next update tick by <see cref="EnsureDocked"/>.
        /// </summary>
        public void SetDockSide(DockSide side)
        {
            _dockSide = side;
        }

        /// <summary>
        /// Insert the panel as the first (top) or last (bottom) child of HDT's
        /// BattlegroundsSessionStackPanel so it sits flush against the session window
        /// and follows it automatically. Re-docks if the chosen side has changed.
        /// </summary>
        private void EnsureDocked()
        {
            if (_panel == null)
                return;

            Panel stack = GetSessionStackPanel();
            if (stack == null)
                return; // overlay not built yet; retry next tick

            bool wrongParent = !ReferenceEquals(_panel.Parent, stack);
            bool wrongSide = _dockedAt != _dockSide;
            if (!wrongParent && !wrongSide)
                return;

            if (_panel.Parent is Panel op) op.Children.Remove(_panel);
            if (_dockSide == DockSide.Top)
                stack.Children.Insert(0, _panel);   // flush above the session
            else
                stack.Children.Add(_panel);         // flush below the session
            _panel.SetDockedAppearance(_dockSide);
            _dockedAt = _dockSide;
            _docked = true;
            FileLogger.Instance.Info($"Docked to {_dockSide.ToString().ToLower()} of session window");
        }

        private void Undock()
        {
            if (_panel?.Parent is Panel parent)
                parent.Children.Remove(_panel);
            _docked = false;
            _dockedAt = null;
        }

        /// <summary>
        /// Match HDT's session scale: HDT applies Config.OverlaySessionRecapScaling/100
        /// to the session control (OverlayWindow.Update.cs); we mirror it so the panel
        /// stays the same size as the session window.
        /// </summary>
        private void ApplySessionScale()
        {
            if (_panel == null || !_docked)
                return;
            double ratio = 1.0;
            try { ratio = Config.Instance.OverlaySessionRecapScaling / 100.0; }
            catch { /* keep default */ }
            if (ratio <= 0) ratio = 1.0;

            // When docked below the session, the session content scales from its
            // top-left but keeps its full unscaled layout slot, leaving a gap below
            // it. Pull the panel up by that gap (sum of the heights stacked above us
            // times the scale shrinkage) so it stays flush with the visible bottom.
            double offsetY = 0;
            if (_dockSide == DockSide.Bottom)
            {
                Panel stack = GetSessionStackPanel();
                if (stack != null)
                {
                    int idx = stack.Children.IndexOf(_panel);
                    double above = 0;
                    for (int i = 0; i < idx; i++)
                        if (stack.Children[i] is FrameworkElement fe)
                            above += fe.ActualHeight;
                    offsetY = -above * (1 - ratio);
                }
            }
            _panel.SetScale(ratio, offsetY);
        }

        private static Panel GetSessionStackPanel()
        {
            var overlay = Core.Overlay;
            if (overlay == null)
                return null;
            // Auto-generated XAML field is internal -> BindingFlags.NonPublic.
            FieldInfo field = overlay.GetType().GetField(
                "BattlegroundsSessionStackPanel",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(overlay) as Panel;
        }

        // ---- Update loop ----------------------------------------------------

        public void OnUpdate()
        {
            EnsureDocked();
            ApplySessionScale();

            if (Core.Game.IsInMenu)
            {
                if (!_isReset)
                {
                    Reset();
                    _isReset = true;
                }
                return;
            }

            if (!Core.Game.IsBattlegroundsMatch)
                return;

            if (_isReset)
            {
                Reset(); // start fresh for this match
                _isReset = false;
                _panel?.ShowMessage("Reading lobby…");
                _ = GetLeaderBoard();
            }

            if (_done || Core.Game.GetTurnNumber() == 0)
                return;

            if (_failToGetData)
            {
                _panel?.ShowMessage("Failed to get data\nCheck log for details");
                ClearMemory();
                _done = true;
                return;
            }

            if (!_leaderBoardReady)
                return;

            if (!TryGetLobbyNames())
                return; // names not visible yet (player must mouse over the leaderboard)

            RenderRows();
            ClearMemory();
            _done = true;
        }

        private void Reset()
        {
            _done = false;
            _failToGetData = false;
            _leaderBoardReady = false;
            _nameErrors = 0;
            ClearMemory();
            if (_panel != null)
                _panel.Visibility = Visibility.Collapsed;
        }

        private void ClearMemory()
        {
            _myName = null;
            _lobbyNames = null;
            _leaderBoard = null;
            _mirror?.Clean();
        }

        // ---- Rendering ------------------------------------------------------

        private void RenderRows()
        {
            string region = GetRegionStr();
            var withMmr = new List<(string Name, int Mmr, bool IsSelf)>();

            foreach (string name in _lobbyNames)
            {
                bool isSelf = !string.IsNullOrEmpty(_myName) && name == _myName;
                int mmr = 0;
                if (_leaderBoard != null && _leaderBoard.TryGetValue(name, out string value))
                {
                    if (!int.TryParse(value, out mmr))
                    {
                        FileLogger.Instance.Warn($"Parse MMR failed, set MMR as 0. player:'{name}' MMR:'{value}'");
                        mmr = 0;
                    }
                }
                withMmr.Add((name, mmr, isSelf));
            }

            // Highest MMR at the top; unknown (0) sinks to the bottom.
            var rows = withMmr
                .OrderByDescending(x => x.Mmr)
                .Select(x => new PlayerRow(
                    x.Name,
                    x.Mmr == 0 ? (region == "CN" ? "-" : "8000↓") : x.Mmr.ToString(),
                    x.IsSelf))
                .ToList();

            _panel?.ShowRows(rows);
        }

        // ---- Leaderboard fetch (adapted from HDT_BGrank) --------------------

        private async Task GetLeaderBoard()
        {
            string region = GetRegionStr();
            if (region == "UNKNOWN")
            {
                FileLogger.Instance.Warn("Failed to get your server region, no data for this match");
                _failToGetData = true;
                return;
            }

            _leaderBoard = new Dictionary<string, string>();
            bool duo = !Core.Game.IsBattlegroundsSoloMatch;
            string path = Path.Combine(Config.AppDataPath, "LobbyMMR",
                duo ? $"LeaderBoard_{region}_duo.txt" : $"LeaderBoard_{region}.txt");

            // Try each source in order until one yields data: our self-hosted
            // cache (US/EU/AP), then the original bgrank service as a backstop.
            foreach (string url in GetLeaderBoardUrls(region, duo))
            {
                if (_leaderBoardReady)
                    break;
                await TryFetchLeaderBoard(url);
            }

            if (_leaderBoardReady)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using StreamWriter writer = new StreamWriter(path);
                    foreach (var player in _leaderBoard)
                        writer.WriteLine($"{player.Key} {player.Value}");
                }
                catch (Exception ex)
                {
                    FileLogger.Instance.Error("Failed to cache leaderboard locally", ex);
                }
                return;
            }

            // Web failed -> fall back to the last cached copy.
            FileLogger.Instance.Info("Web fetch failed, trying local cache…");
            if (File.Exists(path))
            {
                try
                {
                    string line;
                    using StreamReader reader = new StreamReader(path);
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] tmp = line.Split(' ');
                        if (tmp.Length == 2) { _leaderBoard[tmp[0]] = tmp[1]; }
                    }
                    if (_leaderBoard.Count != 0)
                    {
                        FileLogger.Instance.Info("Loaded leaderboard from local cache");
                        _leaderBoardReady = true;
                    }
                    else { _failToGetData = true; }
                }
                catch (Exception ex)
                {
                    FileLogger.Instance.Error("Failed to read local cache", ex);
                    _failToGetData = true;
                }
            }
            else
            {
                FileLogger.Instance.Warn("No local cache available");
                _failToGetData = true;
            }
        }

        /// <summary>
        /// Ordered leaderboard sources to try for a region/mode. US/EU/AP are
        /// served from our self-hosted cache first, then the original bgrank
        /// service. CN is sourced only from bgrank (we don't scrape the separate,
        /// season-bound CN API), so it keeps its original behaviour.
        /// </summary>
        private IEnumerable<string> GetLeaderBoardUrls(string region, bool duo)
        {
            string mode = duo ? "_duo" : "";
            if (region != "CN")
                yield return $"https://zakarulcodes.github.io/hdt-lobbymmr-leaderboard/{region}{mode}.txt";
            yield return $"https://bgrank.fly.dev/{region}{mode}/";
        }

        /// <summary>
        /// Fetch and parse one leaderboard source into <see cref="_leaderBoard"/>,
        /// retrying transient failures. Sets <see cref="_leaderBoardReady"/> on
        /// success so the caller stops trying further sources. Both our cache and
        /// bgrank share the same "name rating" / "\n&lt;br /&gt;" format.
        /// </summary>
        private async Task TryFetchLeaderBoard(string url)
        {
            const int maxTries = 2;
            for (int numTries = 1; numTries <= maxTries && !_leaderBoardReady; numTries++)
            {
                try
                {
                    FileLogger.Instance.Info($"Fetching leaderboard from {url} (try {numTries}/{maxTries})");
                    string response = await _client.GetStringAsync(url);
                    if (string.IsNullOrWhiteSpace(response))
                    {
                        if (numTries < maxTries) { await Task.Delay(3000); }
                        continue;
                    }

                    string[] lines = response.Split(new[] { "\n<br />" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        string[] tmp = line.Split(' ');
                        if (tmp.Length == 2)
                        {
                            string name = tmp[0];
                            string rating = tmp[1];
                            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rating)) { continue; }
                            if (!_leaderBoard.ContainsKey(name)) { _leaderBoard.Add(name, rating); }
                        }
                    }
                    if (_leaderBoard.Count != 0)
                    {
                        FileLogger.Instance.Info($"Loaded {_leaderBoard.Count} leaderboard entries from {url}");
                        _leaderBoardReady = true;
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Instance.Error($"Failed to fetch leaderboard from {url}", ex);
                    if (numTries < maxTries) { await Task.Delay(3000); }
                }
            }
        }

        private string GetRegionStr()
        {
            return Core.Game.CurrentRegion switch
            {
                Region.US => "US",
                Region.EU => "EU",
                Region.ASIA => "AP",
                Region.CHINA => "CN",
                _ => "UNKNOWN"
            };
        }

        // ---- Reading lobby player names from game memory --------------------

        private bool TryGetLobbyNames()
        {
            try
            {
                _myName = _mirror.MyName;
                dynamic leaderboardMgr = _mirror.Root?["PlayerLeaderboardManager"]?["s_instance"];
                if (leaderboardMgr == null) { return false; }

                dynamic[] playerTiles = GetPlayerTiles(leaderboardMgr);
                int count = playerTiles?.Length ?? 0;
                if (count == 0) { return false; }

                var names = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    dynamic playerTile = playerTiles[i];
                    // Name is only populated once the player has moused over the tile.
                    string playerName = playerTile?["m_overlay"]?["m_heroActor"]?["m_playerNameText"]?["m_Text"];
                    if (string.IsNullOrWhiteSpace(playerName)) { return false; }

                    // Strip BattleTag suffix (for users of the BattleTag mod).
                    int idx = playerName.IndexOf('#');
                    if (idx > 0) { playerName = playerName.Substring(0, idx); }

                    if (!names.Contains(playerName)) { names.Add(playerName); }
                }

                _lobbyNames = names;
                return true;
            }
            catch (Exception ex)
            {
                _nameErrors++;
                if (_nameErrors < 5)
                    FileLogger.Instance.Error("Failed to read lobby names", ex);
                else if (_nameErrors == 5)
                    FileLogger.Instance.Error("Failed to read lobby names; further errors suppressed", ex);
                return false;
            }
        }

        // From https://github.com/Zero-to-Heroes/unity-spy-.net4.5
        private dynamic[] GetPlayerTiles(dynamic leaderboardMgr)
        {
            var result = new List<dynamic>();
            dynamic teams = leaderboardMgr["m_teams"]?["_items"];
            if (teams == null) { return result.ToArray(); }

            for (uint i = 0; i < teams.size(); i++)
            {
                dynamic team = teams[i];
                if (team == null) { continue; }
                dynamic tiles = team["m_playerLeaderboardCards"]?["_items"];
                if (tiles == null) { continue; }
                for (uint j = 0; j < tiles.size(); j++)
                    result.Add(tiles[j]);
            }
            return result.ToArray();
        }
    }
}
