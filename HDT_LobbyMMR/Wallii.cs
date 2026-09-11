using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace HDT_LobbyMMR
{
    // ---- Aggregated results the UI consumes -------------------------------

    /// <summary>A lobby player's recent-form stats resolved from wallii.gg.</summary>
    public class WalliiStat
    {
        public int PlayerId;
        public string Region;   // wallii region ("NA"/"EU"/"AP")
        public int Rating;      // wallii's latest rating in the chosen region
        /// <summary>Recent average placement: weekly average, else the day's.</summary>
        public double? Avg;
        public int Games;       // games behind the weekly average
        /// <summary>Streamer channel URL (twitch preferred, else youtube), or null.</summary>
        public string StreamUrl;
        /// <summary>True when wallii reports the streamer is currently live.</summary>
        public bool IsLive;
    }

    /// <summary>Click-through detail: recent inferred games + rating trend.</summary>
    public class WalliiDossier
    {
        public double? WeekAvg;
        public int WeekGames;
        public double? DayAvg;
        public int DayGames;
        /// <summary>Ratings oldest-first, for the sparkline.</summary>
        public List<int> RatingHistory = new List<int>();
        /// <summary>Inferred games, most recent first.</summary>
        public List<GameRecord> Games = new List<GameRecord>();
    }

    public struct GameRecord
    {
        public DateTimeOffset At;
        public double Placement;
        public int Delta;
        public int Ending;
    }

    /// <summary>
    /// Read-only access to the public wallii.gg Supabase (PostgREST) API — the same
    /// backend the wallii.gg site queries from the browser. The anon key is public by
    /// design (shipped in their client bundle, read-only via row-level security).
    /// wallii has no real per-game data: it snapshots the official leaderboard every
    /// few minutes and infers a game (and its placement) from each rating delta.
    ///
    /// Best-effort like the streamer/history feeds: any failure just means no recent-
    /// form chip, never a failed core MMR list.
    /// </summary>
    public class Wallii
    {
        private const string BaseUrl = "https://xtivasurpzvcbomieuba.supabase.co/rest/v1";
        private const string AnonKey =
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Inh0aXZhc3VycHp2Y2JvbWlldWJhIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NDQzMTUzODgsImV4cCI6MjA1OTg5MTM4OH0.Opd3c-esvzBd-CWBDSSV7XFB2JCF2LlyevrE2Yr054U";

        private readonly HttpClient _http;

        // Dossier is fetched on click; cache briefly so repeat clicks don't refetch.
        private class CacheEntry { public WalliiDossier Value; public DateTime At; }
        private static readonly TimeSpan DossierTtl = TimeSpan.FromMinutes(2);
        private readonly ConcurrentDictionary<string, CacheEntry> _dossierCache =
            new ConcurrentDictionary<string, CacheEntry>();

        public Wallii()
        {
            // HDT targets net472, where TLS 1.2 is not always the default.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _http.DefaultRequestHeaders.Add("apikey", AnonKey);
            _http.DefaultRequestHeaders.Add("Authorization", "Bearer " + AnonKey);
            _http.DefaultRequestHeaders.Add("User-Agent", "HDT_LobbyMMR");
        }

        public void Dispose() => _http?.Dispose();

        /// <summary>
        /// Map an HDT region string to wallii's, or null when wallii has no data for it
        /// (UNKNOWN isn't resolved yet). wallii tracks NA/EU/AP/CN.
        /// </summary>
        public static string MapRegion(string hdtRegion)
        {
            switch (hdtRegion)
            {
                case "US": return "NA";
                case "EU": return "EU";
                case "AP": return "AP";
                case "CN": return "CN";
                default: return null;
            }
        }

        /// <summary>
        /// Resolve recent-form stats for a set of lobby names in one batched round of
        /// queries. Only rows from <paramref name="walliiRegion"/> are used — a lobby is
        /// played on exactly one ladder, so another region's row is a different person or
        /// an unrelated ladder. Names wallii doesn't track are simply absent from the map.
        /// The returned map is keyed by the original lobby name (case-insensitive).
        /// </summary>
        public async Task<Dictionary<string, WalliiStat>> GetLobbyStatsAsync(
            IReadOnlyList<string> lobbyNames, string walliiRegion, string gameMode)
        {
            var result = new Dictionary<string, WalliiStat>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(walliiRegion) || lobbyNames == null || lobbyNames.Count == 0)
                return result;

            var lower = lobbyNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(n => n, n => n.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
            if (lower.Count == 0)
                return result;

            // players + channels are independent; run them together. Channels is
            // best-effort (streamer dot only) — its failure must not sink the stats.
            var playersTask = LookupPlayersAsync(lower.Values);
            var channelsTask = GetChannelsAsync(lower.Values);
            await Task.WhenAll(Swallow(channelsTask), playersTask).ConfigureAwait(false);
            var players = playersTask.Result;
            var channels = channelsTask.Status == TaskStatus.RanToCompletion
                ? channelsTask.Result
                : new List<ChannelJson>();

            // wallii keeps a single identity per (lowercased) name; take the first if
            // the API ever returns more than one for a name.
            var byName = players
                .GroupBy(p => p.PlayerName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var stats = players.Count > 0
                ? await GetDailyStatsAsync(players.Select(p => p.PlayerId), gameMode).ConfigureAwait(false)
                : new List<DailyStatsJson>();

            // Latest row per (player, region).
            var latestPerRegion = stats
                .GroupBy(s => new { s.PlayerId, s.Region })
                .Select(g => g.OrderByDescending(s => s.DayStart, StringComparer.Ordinal).First())
                .GroupBy(s => s.PlayerId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var kv in lower)
            {
                if (!byName.TryGetValue(kv.Value, out var player) ||
                    !latestPerRegion.TryGetValue(player.PlayerId, out var regionRows))
                    continue;

                var row = regionRows.FirstOrDefault(r =>
                    string.Equals(r.Region, walliiRegion, StringComparison.OrdinalIgnoreCase));
                if (row == null)
                    continue; // tracked, but only on another ladder — not this lobby's player

                result[kv.Key] = new WalliiStat
                {
                    PlayerId = player.PlayerId,
                    Region = row.Region,
                    Rating = row.Rating,
                    Avg = row.WeeklyAvg ?? row.DayAvg,
                    Games = row.WeeklyGamesPlayed,
                };
            }

            // Fold in streamer channels (a superset of the tracked players). A player
            // with only a channel and no stats still gets an entry — no chip, but a
            // streamer dot + live status.
            var chByName = channels
                .GroupBy(c => c.Player, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var kv in lower)
            {
                if (!chByName.TryGetValue(kv.Value, out var ch))
                    continue;
                string url = ChannelUrl(ch);
                if (url == null)
                    continue;
                if (!result.TryGetValue(kv.Key, out var stat))
                    result[kv.Key] = stat = new WalliiStat { Region = walliiRegion };
                stat.StreamUrl = url;
                stat.IsLive = ch.Live;
            }
            return result;
        }

        private static string ChannelUrl(ChannelJson c)
        {
            if (!string.IsNullOrWhiteSpace(c.Channel)) return "https://twitch.tv/" + c.Channel;
            if (!string.IsNullOrWhiteSpace(c.Youtube)) return "https://youtube.com/@" + c.Youtube;
            return null;
        }

        private static async Task Swallow(Task t)
        {
            try { await t.ConfigureAwait(false); } catch { /* best-effort */ }
        }

        /// <summary>
        /// Recent inferred games + rating trend for one player, from leaderboard
        /// snapshots. Cached briefly. Returns null on failure or no data.
        /// </summary>
        public async Task<WalliiDossier> GetDossierAsync(int playerId, string region, string gameMode, int recentGames = 10)
        {
            var key = $"{playerId}|{region}|{gameMode}";
            if (_dossierCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < DossierTtl)
                return hit.Value;

            try
            {
                // 200 snapshots ≈ a week of games even for very active players.
                var snapshots = await GetSnapshotsAsync(playerId, region, gameMode, 200).ConfigureAwait(false);
                if (snapshots.Count == 0)
                    return null;

                var records = BuildGameRecords(snapshots);
                var localToday = DateTime.Now.Date;
                var today = records.Where(r => r.At.ToLocalTime().Date == localToday).ToList();
                var week = records.Where(r => r.At >= DateTimeOffset.UtcNow.AddDays(-7)).ToList();

                var dossier = new WalliiDossier
                {
                    RatingHistory = snapshots.OrderBy(s => s.SnapshotTime).Select(s => s.Rating).ToList(),
                    Games = records.Take(recentGames).ToList(),
                    DayAvg = Average(today),
                    DayGames = today.Count,
                    WeekAvg = Average(week),
                    WeekGames = week.Count,
                };
                _dossierCache[key] = new CacheEntry { Value = dossier, At = DateTime.UtcNow };
                return dossier;
            }
            catch (Exception ex)
            {
                FileLogger.Instance.Debug($"wallii dossier fetch failed ({playerId}): {ex.Message}");
                return null;
            }
        }

        // ---- Raw API calls --------------------------------------------------

        private async Task<List<PlayerRowJson>> LookupPlayersAsync(IEnumerable<string> lowercaseNames)
        {
            var inList = BuildQuotedInList(lowercaseNames);
            var url = $"{BaseUrl}/players?select=player_id,player_name,display_name&player_name=in.{inList}";
            return await GetAsync<List<PlayerRowJson>>(url).ConfigureAwait(false) ?? new List<PlayerRowJson>();
        }

        private async Task<List<DailyStatsJson>> GetDailyStatsAsync(IEnumerable<int> playerIds, string gameMode, int daysBack = 10)
        {
            var ids = playerIds.Distinct().ToList();
            if (ids.Count == 0)
                return new List<DailyStatsJson>();
            // InvariantCulture is essential: a non-Gregorian default calendar would
            // format a future "year" and silently empty the result set.
            var since = DateTime.UtcNow.AddDays(-daysBack).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var url = $"{BaseUrl}/daily_leaderboard_stats" +
                      "?select=player_id,region,day_start,rating,rank,weekly_games_played,day_avg,weekly_avg" +
                      $"&player_id=in.({string.Join(",", ids)})" +
                      $"&game_mode=eq.{Uri.EscapeDataString(gameMode)}" +
                      $"&day_start=gte.{since}" +
                      "&order=day_start.desc&limit=1000";
            return await GetAsync<List<DailyStatsJson>>(url).ConfigureAwait(false) ?? new List<DailyStatsJson>();
        }

        private async Task<List<ChannelJson>> GetChannelsAsync(IEnumerable<string> lowercaseNames)
        {
            var inList = BuildQuotedInList(lowercaseNames);
            var url = $"{BaseUrl}/channels?select=channel,player,live,youtube&player=in.{inList}";
            return await GetAsync<List<ChannelJson>>(url).ConfigureAwait(false) ?? new List<ChannelJson>();
        }

        private async Task<List<SnapshotJson>> GetSnapshotsAsync(int playerId, string region, string gameMode, int limit)
        {
            var url = $"{BaseUrl}/leaderboard_snapshots" +
                      "?select=rating,snapshot_time" +
                      $"&player_id=eq.{playerId}" +
                      $"&region=eq.{Uri.EscapeDataString(region)}" +
                      $"&game_mode=eq.{Uri.EscapeDataString(gameMode)}" +
                      "&order=snapshot_time.desc" +
                      $"&limit={limit}";
            return await GetAsync<List<SnapshotJson>>(url).ConfigureAwait(false) ?? new List<SnapshotJson>();
        }

        private async Task<T> GetAsync<T>(string url) where T : class
        {
            Exception last = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var response = await _http.GetAsync(url).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        // PostgREST answers "no rows" with "[]", never an empty body —
                        // an empty 200 is a degraded edge, so retry rather than accept it.
                        if (string.IsNullOrWhiteSpace(json))
                            throw new Exception("empty body");
                        return JsonConvert.DeserializeObject<T>(json);
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
            FileLogger.Instance.Debug($"wallii request failed: {url} ({last?.Message})");
            return null;
        }

        /// <summary>Builds a PostgREST quoted, URL-encoded in-list: ("a","b").</summary>
        private static string BuildQuotedInList(IEnumerable<string> values)
        {
            var quoted = values.Select(v => '"' + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"');
            return Uri.EscapeDataString("(" + string.Join(",", quoted) + ")");
        }

        // ---- Placement estimation (port of wallii's calculatePlacements.ts) -

        private static readonly double[] Placements =
            { 1, 2, 3, 3.5, 4, 4.5, 5, 5.5, 6, 6.5, 7, 7.5, 8 };

        // A real game moves rating by at most a couple hundred; a bigger jump is
        // structural (season reset, data correction) and isn't a played game.
        private const int MaxPlausibleGameDelta = 500;

        internal static double EstimatePlacement(double start, double end)
        {
            var gain = end - start;
            var dexAvg = start < 8200 ? start : start - 0.85 * (start - 10000);

            var best = Placements[0];
            var bestDelta = double.PositiveInfinity;
            foreach (var p in Placements)
            {
                var avgOpp = start - 148.1181435 * (100 - ((p - 1) * (200.0 / 7) + gain));
                if (avgOpp > 10000)
                    continue;
                var delta = Math.Abs(dexAvg - avgOpp);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>Turn snapshots into inferred games, most recent first. Consecutive
        /// snapshots with identical rating are "no game" and skipped.</summary>
        internal static List<GameRecord> BuildGameRecords(IEnumerable<SnapshotJson> snapshots)
        {
            var sorted = snapshots.OrderBy(s => s.SnapshotTime).ToList();
            var records = new List<GameRecord>();
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var start = sorted[i];
                var end = sorted[i + 1];
                var delta = end.Rating - start.Rating;
                if (delta == 0 || Math.Abs(delta) > MaxPlausibleGameDelta)
                    continue;
                records.Add(new GameRecord
                {
                    At = end.SnapshotTime,
                    Placement = EstimatePlacement(start.Rating, end.Rating),
                    Delta = delta,
                    Ending = end.Rating,
                });
            }
            records.Reverse();
            return records;
        }

        private static double? Average(IReadOnlyList<GameRecord> records) =>
            records == null || records.Count == 0 ? (double?)null : Math.Round(records.Average(r => r.Placement), 2);

        // ---- JSON row shapes ------------------------------------------------

        internal class PlayerRowJson
        {
            [JsonProperty("player_id")] public int PlayerId { get; set; }
            [JsonProperty("player_name")] public string PlayerName { get; set; }
            [JsonProperty("display_name")] public string DisplayName { get; set; }
        }

        internal class DailyStatsJson
        {
            [JsonProperty("player_id")] public int PlayerId { get; set; }
            [JsonProperty("region")] public string Region { get; set; }
            [JsonProperty("day_start")] public string DayStart { get; set; }
            [JsonProperty("rating")] public int Rating { get; set; }
            [JsonProperty("rank")] public int Rank { get; set; }
            [JsonProperty("weekly_games_played")] public int WeeklyGamesPlayed { get; set; }
            [JsonProperty("day_avg")] public double? DayAvg { get; set; }
            [JsonProperty("weekly_avg")] public double? WeeklyAvg { get; set; }
        }

        internal class SnapshotJson
        {
            [JsonProperty("rating")] public int Rating { get; set; }
            [JsonProperty("snapshot_time")] public DateTimeOffset SnapshotTime { get; set; }
        }

        internal class ChannelJson
        {
            [JsonProperty("channel")] public string Channel { get; set; }
            [JsonProperty("player")] public string Player { get; set; }
            [JsonProperty("live")] public bool Live { get; set; }
            [JsonProperty("youtube")] public string Youtube { get; set; }
        }

        // ---- Self-check (debug builds only) ---------------------------------

        [Conditional("DEBUG")]
        internal static void SelfTest()
        {
            // A ~+70 gain from a mid rating should read as a strong (low) placement,
            // a big loss as a weak (high) one; the estimator must be monotonic-ish.
            var win = EstimatePlacement(9000, 9075);
            var loss = EstimatePlacement(9000, 8925);
            Debug.Assert(win < loss, $"expected win<loss, got {win} vs {loss}");

            // Structural jumps (season reset) must not be counted as games.
            var snaps = new List<SnapshotJson>
            {
                new SnapshotJson { Rating = 9000, SnapshotTime = DateTimeOffset.UtcNow.AddHours(-3) },
                new SnapshotJson { Rating = 9075, SnapshotTime = DateTimeOffset.UtcNow.AddHours(-2) }, // a game
                new SnapshotJson { Rating = 5000, SnapshotTime = DateTimeOffset.UtcNow.AddHours(-1) }, // reset, skip
            };
            Debug.Assert(BuildGameRecords(snaps).Count == 1, "structural jump was counted as a game");
        }
    }
}
