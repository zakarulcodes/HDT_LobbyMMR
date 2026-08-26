using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using HearthDb.Enums;
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
        // Set true when the streamer-data fetch attempt finishes, success or
        // not — lets OnUpdate wait a moment for it so icons aren't lost to a
        // race where the (usually smaller/faster) leaderboard fetch happens
        // to finish first, without ever blocking indefinitely on a dead source.
        private bool _streamersReady = false;
        private int _nameErrors = 0;
        private bool _docked = false;
        private DockSide _dockSide = DockSide.Top;
        private DockSide? _dockedAt = null;
        private bool _showStreamerIcon = true;
        private bool _showRank = true;

        private string _myName;
        // One inner list per lobby team (size 1 in solo, 2 in duo), in the game's
        // own team order — preserved so duo rows can be grouped by teammate.
        private List<List<string>> _lobbyTeams;
        // Hero card id -> lobby name, captured from the leaderboard tiles when names
        // load. Bridges the log-tracked hero entity (keyed by card id) to the name.
        private Dictionary<string, string> _nameByHeroCard;
        // Name -> elimination ordinal (0 = first out), assigned once when a player is
        // first seen dead (health <= 0). Grow-only: a player stays eliminated for the
        // rest of the match even when the leaderboard tile later reads blank (e.g. it
        // resets during combat), so a greyed row never flips back to white. Doubles as
        // the "is eliminated" set. Also gives the greyed rows their death-order sort.
        private Dictionary<string, int> _elimOrder;
        private int _elimSeq;
        // Change-detection so we only rebuild the panel when the eliminated set
        // changes, not every tick.
        private bool _hasRendered;
        private string _elimKey;
        // Rank is each player's 1-based position on the region leaderboard (all
        // sources list entries in rank order, so it's the entry's index).
        private Dictionary<string, (string Rating, int Rank)> _leaderBoard;
        // Name -> channel URL (Twitch preferred, else YouTube) for known streamers.
        // Best-effort: a missed/failed fetch just means no icons are shown, it never
        // blocks or fails the core MMR feature.
        private Dictionary<string, string> _streamers;
        // Name -> that player's finished-season leaderboard history for the region,
        // newest season first. Best-effort like _streamers: a missing file just means
        // no hover tooltip. Data is a one-time scrape of Blizzard's past-season
        // leaderboards and never changes, so it's cached locally after first fetch.
        private Dictionary<string, List<(int Season, int Rank, int Rating)>> _history;
        private bool _historyReady = false;

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

        /// <summary>Show/hide the red "known streamer" dot next to player names.</summary>
        public void SetShowStreamerIcon(bool show)
        {
            _showStreamerIcon = show;
        }

        /// <summary>Show/hide each player's leaderboard rank position (e.g. "#42").</summary>
        public void SetShowRank(bool show)
        {
            _showRank = show;
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
                _ = GetStreamers();
                _ = GetHistory();
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

            if (!_leaderBoardReady || !_streamersReady || !_historyReady)
                return;

            // Refresh names/hero-card ids whenever the leaderboard tiles are readable
            // (also picks up a hero transform). The first read needs a hover; once we
            // have the names, later ticks keep going even if the tiles blank out during
            // combat, so grey-out stays live.
            TryGetLobbyNames();
            if (_lobbyTeams == null)
                return; // never captured yet — waiting on the initial hover

            // Elimination is read passively from HDT's log-tracked entities each tick,
            // so players grey out live without hovering. Only rebuild the panel when the
            // eliminated set changes; _elimOrder only grows, so a count change = a new out.
            UpdateElimination();
            string key = (_elimOrder?.Count ?? 0).ToString();
            if (!_hasRendered || key != _elimKey)
            {
                RenderRows();
                _hasRendered = true;
                _elimKey = key;
            }
        }

        private void Reset()
        {
            _done = false;
            _failToGetData = false;
            _leaderBoardReady = false;
            _streamersReady = false;
            _historyReady = false;
            _nameErrors = 0;
            _hasRendered = false;
            _elimKey = null;
            ClearMemory();
            if (_panel != null)
                _panel.Visibility = Visibility.Collapsed;
        }

        private void ClearMemory()
        {
            _myName = null;
            _lobbyTeams = null;
            _nameByHeroCard = null;
            _elimOrder = null;
            _elimSeq = 0;
            _leaderBoard = null;
            _streamers = null;
            _history = null;
            _mirror?.Clean();
        }

        // ---- Rendering ------------------------------------------------------

        private void RenderRows()
        {
            string region = GetRegionStr();
            if (!Core.Game.IsBattlegroundsSoloMatch)
                RenderGroupedRows(region);
            else
                RenderFlatRows(region);
        }

        /// <summary>Solo lobby: every player in one flat list, highest MMR first.</summary>
        private void RenderFlatRows(string region)
        {
            var withMmr = new List<(string Name, int Mmr, int Rank, bool IsSelf)>();
            foreach (List<string> team in _lobbyTeams)
                foreach (string name in team)
                {
                    bool isSelf = !string.IsNullOrEmpty(_myName) && name == _myName;
                    (int mmr, int rank) = LookupMmrRank(name, isSelf);
                    withMmr.Add((name, mmr, rank, isSelf));
                }

            // Alive players first (highest MMR at top, unknown 0 sinks to the bottom),
            // then eliminated players greyed below in reverse death order (most-recently
            // eliminated just under the survivors, first-out at the very bottom).
            var rows = withMmr
                .OrderBy(x => IsElim(x.Name) ? 1 : 0)
                .ThenByDescending(x => IsElim(x.Name) ? ElimOrd(x.Name) : x.Mmr)
                .Select(x => new PlayerRow(
                    x.Name, FormatMmr(x.Mmr, region),
                    _showRank ? FormatRank(x.Rank) : "",
                    x.IsSelf,
                    IsElim(x.Name),
                    _showStreamerIcon ? LookupStreamUrl(x.Name) : null,
                    LookupHistory(x.Name)))
                .ToList();

            _panel?.ShowRows(rows);
        }

        /// <summary>
        /// Duo lobby: players grouped by teammate pair, each still showing their
        /// own individual MMR/rank (no averaging). Within a team the higher-MMR
        /// teammate is listed first; teams are ordered by their higher-MMR member
        /// so the strongest team sits at the top of the panel.
        /// </summary>
        private void RenderGroupedRows(string region)
        {
            var teams = new List<(int MaxMmr, bool IsDead, int ElimOrd, bool HasSelf, List<PlayerRow> Rows)>();

            foreach (List<string> team in _lobbyTeams)
            {
                var members = team
                    .Select(name =>
                    {
                        bool isSelf = !string.IsNullOrEmpty(_myName) && name == _myName;
                        (int mmr, int rank) = LookupMmrRank(name, isSelf);
                        return (Name: name, Mmr: mmr, Rank: rank, IsSelf: isSelf);
                    })
                    .OrderByDescending(m => m.Mmr) // higher-MMR teammate first
                    .ToList();

                int maxMmr = members.Count > 0 ? members[0].Mmr : 0;
                bool hasSelf = members.Any(m => m.IsSelf);
                var rows = members
                    .Select(m => new PlayerRow(
                        m.Name, FormatMmr(m.Mmr, region),
                        _showRank ? FormatRank(m.Rank) : "",
                        m.IsSelf,
                        IsElim(m.Name),
                        _showStreamerIcon ? LookupStreamUrl(m.Name) : null,
                        LookupHistory(m.Name)))
                    .ToList();

                // A team is out once both teammates are eliminated; its ordinal is
                // the last member to die (so teams sort by when the team went out).
                bool isDead = team.All(n => IsElim(n));
                int elimOrd = isDead ? team.Max(n => ElimOrd(n)) : -1;
                teams.Add((maxMmr, isDead, elimOrd, hasSelf, rows));
            }

            // Alive teams first (best by higher-MMR member at top), then eliminated
            // teams greyed below in reverse death order. Label numbering follows the
            // final order, so "Team 1" is always the top team.
            var ordered = teams
                .OrderBy(t => t.IsDead ? 1 : 0)
                .ThenByDescending(t => t.IsDead ? t.ElimOrd : t.MaxMmr)
                .Select((t, i) => (TeamNumber: i + 1, t.HasSelf, t.Rows))
                .ToList();

            _panel?.ShowTeams(ordered);
        }

        private (int Mmr, int Rank) LookupMmrRank(string name, bool isSelf = false)
        {
            int mmr = 0;
            int rank = 0;
            if (_leaderBoard != null && _leaderBoard.TryGetValue(name, out var entry))
            {
                if (int.TryParse(entry.Rating, out mmr))
                    rank = entry.Rank;
                else
                    FileLogger.Instance.Warn($"Parse MMR failed, set MMR as 0. player:'{name}' MMR:'{entry.Rating}'");
            }

            // Our own MMR comes straight from the game (via HDT), which is always
            // current — the scraped leaderboard can lag behind. Rank still comes
            // from the leaderboard entry. Below 8000 we show the same "8000↓" as
            // everyone else (the leaderboard only lists 8000+ players), so a live
            // value under the cutoff maps to 0 even if a stale entry has us above it.
            if (isSelf)
            {
                int live = GetMyCurrentMmr();
                if (live > 0)
                {
                    if (live >= 8000)
                    {
                        mmr = live;
                    }
                    else
                    {
                        // Below the cutoff we show "8000↓" with no rank, like
                        // everyone else — drop the stale leaderboard rank too.
                        mmr = 0;
                        rank = 0;
                    }
                }
            }
            return (mmr, rank);
        }

        /// <summary>
        /// The local player's current rating as the game itself reports it
        /// (solo or duos to match the current lobby), or 0 if unavailable.
        /// </summary>
        private static int GetMyCurrentMmr()
        {
            try
            {
                return Core.Game.CurrentBattlegroundsRating ?? 0;
            }
            catch (Exception ex)
            {
                FileLogger.Instance.Warn($"Failed to read own MMR from game: {ex.Message}");
                return 0;
            }
        }

        private static string FormatMmr(int mmr, string region) =>
            mmr == 0 ? (region == "CN" ? "-" : "8000↓") : mmr.ToString();

        private static string FormatRank(int rank) => rank > 0 ? $"#{rank}" : "";

        private bool IsElim(string name) =>
            _elimOrder != null && _elimOrder.ContainsKey(name);

        private int ElimOrd(string name) =>
            _elimOrder != null && _elimOrder.TryGetValue(name, out int o) ? o : -1;

        private string LookupStreamUrl(string name) =>
            _streamers != null && _streamers.TryGetValue(name, out string url) ? url : null;

        /// <summary>
        /// This player's past-season ranks as preformatted tooltip lines, newest
        /// season first (e.g. "S14   #42   11441"), or null if none are known.
        /// </summary>
        private IReadOnlyList<string> LookupHistory(string name)
        {
            if (_history == null || !_history.TryGetValue(name, out var seasons))
                return null;
            return seasons
                .OrderByDescending(s => s.Season)
                .Select(s => $"S{s.Season}   #{s.Rank}   {s.Rating}")
                .ToList();
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

            // Case-insensitive so a lobby name matches its leaderboard/streamer
            // entry regardless of casing differences between data sources.
            _leaderBoard = new Dictionary<string, (string Rating, int Rank)>(StringComparer.OrdinalIgnoreCase);
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
                        writer.WriteLine($"{player.Key} {player.Value.Rating} {player.Value.Rank}");
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
                        // 3 tokens = current format (name rating rank); 2 tokens
                        // handles a cache file written before rank was tracked.
                        string[] tmp = line.Split(' ');
                        if (tmp.Length == 3 && int.TryParse(tmp[2], out int rank))
                            _leaderBoard[tmp[0]] = (tmp[1], rank);
                        else if (tmp.Length == 2)
                            _leaderBoard[tmp[0]] = (tmp[1], 0);
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

                    // Entries are in leaderboard rank order, so each entry's
                    // 1-based position in the list is its rank.
                    string[] lines = response.Split(new[] { "\n<br />" }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string[] tmp = lines[i].Split(' ');
                        if (tmp.Length == 2)
                        {
                            string name = tmp[0];
                            string rating = tmp[1];
                            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rating)) { continue; }
                            if (!_leaderBoard.ContainsKey(name)) { _leaderBoard.Add(name, (rating, i + 1)); }
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

        // ---- Streamer data fetch --------------------------------------------

        private const string StreamersUrl = "https://zakarulcodes.github.io/hdt-lobbymmr-streamers/streamers.txt";

        /// <summary>
        /// Best-effort fetch of the known-streamer list (see hdt-lobbymmr-streamers
        /// repo). Failures are logged and swallowed — a missing streamer icon is
        /// never worth failing the core MMR feature over.
        /// </summary>
        private async Task GetStreamers()
        {
            try
            {
                await FetchStreamers();
            }
            finally
            {
                // Set regardless of outcome (success, failure, or no data) so
                // OnUpdate's gate on this flag can't wait forever on a source
                // that's down — it just renders without icons in that case.
                _streamersReady = true;
            }
        }

        private async Task FetchStreamers()
        {
            string path = Path.Combine(Config.AppDataPath, "LobbyMMR", "Streamers.txt");
            string response = null;
            try
            {
                response = await _client.GetStringAsync(StreamersUrl);
            }
            catch (Exception ex)
            {
                FileLogger.Instance.Warn($"Failed to fetch streamer data: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(response))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, response);
                }
                catch (Exception ex)
                {
                    FileLogger.Instance.Error("Failed to cache streamer data locally", ex);
                }
            }
            else if (File.Exists(path))
            {
                try { response = File.ReadAllText(path); }
                catch (Exception ex) { FileLogger.Instance.Error("Failed to read cached streamer data", ex); }
            }

            if (string.IsNullOrWhiteSpace(response))
                return;

            var streamers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in response.Split(new[] { "\n<br />" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] tmp = line.Split(' ');
                if (tmp.Length != 3) continue;
                string url = tmp[1] != "-" ? tmp[1] : (tmp[2] != "-" ? tmp[2] : null);
                if (url != null) streamers[tmp[0]] = url;
            }
            _streamers = streamers;
            FileLogger.Instance.Info($"Loaded {_streamers.Count} known streamers");
        }

        // ---- Past-season history fetch --------------------------------------

        /// <summary>
        /// Best-effort fetch of the region's finished-season leaderboard history
        /// (see the scraped {region}_history.txt files). Failures are logged and
        /// swallowed — a missing hover tooltip is never worth failing the core MMR
        /// feature over. Sets <see cref="_historyReady"/> regardless of outcome so
        /// OnUpdate's gate can't wait forever on a source that's down.
        /// </summary>
        private async Task GetHistory()
        {
            try
            {
                await FetchHistory();
            }
            finally
            {
                _historyReady = true;
            }
        }

        private async Task FetchHistory()
        {
            string region = GetRegionStr();
            // CN isn't part of the scraped past-season set (separate, season-bound
            // API), so there's no file to fetch — just render without tooltips.
            if (region == "UNKNOWN" || region == "CN")
                return;

            string url = $"https://zakarulcodes.github.io/hdt-lobbymmr-leaderboard/{region}_history.txt";
            string path = Path.Combine(Config.AppDataPath, "LobbyMMR", $"{region}_history.txt");
            string response = null;
            try
            {
                response = await _client.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                FileLogger.Instance.Warn($"Failed to fetch history data: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(response))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, response);
                }
                catch (Exception ex)
                {
                    FileLogger.Instance.Error("Failed to cache history data locally", ex);
                }
            }
            else if (File.Exists(path))
            {
                // Finished-season data never changes, so a local copy is as good as
                // the network one — fall back to it whenever the fetch comes up empty.
                try { response = File.ReadAllText(path); }
                catch (Exception ex) { FileLogger.Instance.Error("Failed to read cached history data", ex); }
            }

            if (string.IsNullOrWhiteSpace(response))
                return;

            var history = new Dictionary<string, List<(int Season, int Rank, int Rating)>>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in response.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // name<TAB>rank<TAB>rating<TAB>season
                string[] tmp = line.TrimEnd('\r').Split('\t');
                if (tmp.Length != 4) continue;
                if (!int.TryParse(tmp[1], out int rank)) continue;
                if (!int.TryParse(tmp[2], out int rating)) continue;
                if (!int.TryParse(tmp[3], out int season)) continue;
                if (!history.TryGetValue(tmp[0], out var seasons))
                    history[tmp[0]] = seasons = new List<(int, int, int)>();
                // Names carry no discriminator, so a common name (e.g. "jeef") can
                // match several accounts in one season. Keep the top-rated entry per
                // season, matching how the live leaderboard resolves name collisions
                // (highest-ranked wins) — one clean row per season in the tooltip.
                int i = seasons.FindIndex(e => e.Season == season);
                if (i < 0) seasons.Add((season, rank, rating));
                else if (rating > seasons[i].Rating) seasons[i] = (season, rank, rating);
            }
            _history = history;
            FileLogger.Instance.Info($"Loaded history for {_history.Count} players ({region})");
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

                List<List<dynamic>> teamTiles = GetPlayerTeams(leaderboardMgr);
                int count = teamTiles.Sum(t => t.Count);
                if (count == 0) { return false; }

                var nameByHeroCard = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>();
                var teams = new List<List<string>>();
                foreach (List<dynamic> tiles in teamTiles)
                {
                    var teamNames = new List<string>();
                    foreach (dynamic playerTile in tiles)
                    {
                        dynamic hero = playerTile?["m_overlay"]?["m_heroActor"];

                        // Name is only populated once the player has moused over the tile.
                        string playerName = hero?["m_playerNameText"]?["m_Text"];
                        if (string.IsNullOrWhiteSpace(playerName)) { return false; }

                        // Strip BattleTag suffix (for users of the BattleTag mod).
                        int idx = playerName.IndexOf('#');
                        if (idx > 0) { playerName = playerName.Substring(0, idx); }

                        // Each tile's game entity carries the hero's card id. It's unique
                        // per lobby, so it bridges this name to the log-tracked hero
                        // entity, whose live health drives passive grey-out (UpdateElimination).
                        string heroCard = playerTile?["m_entity"]?["m_cardIdInternal"];
                        if (!string.IsNullOrEmpty(heroCard))
                            nameByHeroCard[heroCard] = playerName;

                        if (seen.Add(playerName)) { teamNames.Add(playerName); }
                    }
                    if (teamNames.Count > 0) { teams.Add(teamNames); }
                }

                _lobbyTeams = teams;
                _nameByHeroCard = nameByHeroCard;
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

        // ---- Passive elimination from HDT's log-tracked entities ------------

        /// <summary>
        /// Mark players eliminated from the game entities HDT parses from the power log
        /// (no hover, no memory read, stays correct through combat). A leaderboard hero
        /// is out when HEALTH + ARMOR - DAMAGE &lt;= 0. Bridged to the lobby name by the
        /// hero's card id (unique per lobby). Grow-only: once out, stays greyed.
        /// </summary>
        private void UpdateElimination()
        {
            if (_nameByHeroCard == null)
                return;
            var ents = Core.Game?.Entities;
            if (ents == null)
                return;
            if (_elimOrder == null)
                _elimOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in ents.Values)
            {
                if (e == null || !e.HasTag(GameTag.PLAYER_LEADERBOARD_PLACE))
                    continue;
                int health = e.GetTag(GameTag.HEALTH);
                if (health <= 0)
                    continue; // hero not initialised yet (pre-pick); can't be dead
                if (health + e.GetTag(GameTag.ARMOR) - e.GetTag(GameTag.DAMAGE) > 0)
                    continue; // still alive
                string card = e.CardId;
                if (!string.IsNullOrEmpty(card) && _nameByHeroCard.TryGetValue(card, out string name)
                    && !_elimOrder.ContainsKey(name))
                    _elimOrder[name] = _elimSeq++;
            }
        }

        // From https://github.com/Zero-to-Heroes/unity-spy-.net4.5
        private List<List<dynamic>> GetPlayerTeams(dynamic leaderboardMgr)
        {
            var result = new List<List<dynamic>>();
            dynamic teams = leaderboardMgr["m_teams"]?["_items"];
            if (teams == null) { return result; }

            for (uint i = 0; i < teams.size(); i++)
            {
                dynamic team = teams[i];
                if (team == null) { continue; }
                dynamic tiles = team["m_playerLeaderboardCards"]?["_items"];
                if (tiles == null) { continue; }
                var tileList = new List<dynamic>();
                for (uint j = 0; j < tiles.size(); j++)
                    tileList.Add(tiles[j]);
                result.Add(tileList);
            }
            return result;
        }
    }
}
