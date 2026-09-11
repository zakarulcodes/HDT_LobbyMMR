using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Hearthstone_Deck_Tracker;

namespace HDT_LobbyMMR
{
    /// <summary>A single player row in the lobby-MMR list.</summary>
    public class PlayerRow
    {
        public string Name;
        public string Mmr;
        /// <summary>Leaderboard rank display text (e.g. "#42"), or "" if unranked.</summary>
        public string Rank;
        public bool IsSelf;
        /// <summary>True once this player has been eliminated from the lobby.</summary>
        public bool IsEliminated;
        /// <summary>Twitch/YouTube channel URL if this player is a known streamer, else null.</summary>
        public string StreamUrl;
        /// <summary>Preformatted past-season lines (newest first) for the hover tooltip,
        /// e.g. "S14   #42   11441". Empty when no history is known for this player.</summary>
        public IReadOnlyList<string> History;
        /// <summary>Recent average placement from wallii.gg (lower is better), or null
        /// when unknown — drives the colored avg chip.</summary>
        public double? Avg;
        /// <summary>wallii player id (0 = none): enables the click-to-open dossier.</summary>
        public int WalliiPlayerId;
        /// <summary>wallii region for the dossier snapshot query ("NA"/"EU"/"AP").</summary>
        public string WalliiRegion;
        /// <summary>True when this streamer is currently live (wallii source only).</summary>
        public bool IsLive;
        /// <summary>Lifetime count of lobbies shared with this player (incl. the current
        /// one), 0 if never tracked before — shown in the hover box.</summary>
        public int SeenCount;

        public PlayerRow(string name, string mmr, string rank, bool isSelf, bool isEliminated,
            string streamUrl = null, IReadOnlyList<string> history = null,
            double? avg = null, int walliiPlayerId = 0, string walliiRegion = null, bool isLive = false,
            int seenCount = 0)
        {
            Name = name;
            Mmr = mmr;
            Rank = rank;
            IsSelf = isSelf;
            IsEliminated = isEliminated;
            StreamUrl = streamUrl;
            History = history;
            Avg = avg;
            WalliiPlayerId = walliiPlayerId;
            WalliiRegion = walliiRegion;
            IsLive = isLive;
            SeenCount = seenCount;
        }
    }

    public partial class LobbyMmrPanel : UserControl
    {
        private static readonly Brush NameBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE3, 0xE3));
        private static readonly Brush MmrBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private static readonly Brush RankBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x94, 0x94));
        private static readonly Brush TeamLabelBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0x72, 0x75));
        private static readonly Brush DividerBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x52, 0x56));
        // Gold accent for the local player, matching HDT's highlight tone.
        private static readonly Brush SelfBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xA4, 0x41));
        private static readonly Brush SelfRowBg = new SolidColorBrush(Color.FromArgb(0x22, 0xD9, 0xA4, 0x41));
        // "Known streamer" marker dot: muted purple when offline, bright red when live.
        private static readonly Brush StreamerBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x7A, 0xA5));
        private static readonly Brush LiveBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x30, 0x30));
        // Muted grey for eliminated players (and a fully-eliminated duo team header).
        private static readonly Brush EliminatedBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0x72, 0x75));
        private static readonly Brush SubtleBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x94, 0x94));
        private static readonly Brush SparkBrush = new SolidColorBrush(Color.FromRgb(0x7F, 0xB0, 0xE0));

        /// <summary>Color an average placement best→worst: 1 green, 4 yellow, 8 red.</summary>
        private static Brush AvgBrush(double avg)
        {
            avg = Math.Max(1.0, Math.Min(8.0, avg));
            Color green = Color.FromRgb(0x5F, 0xD0, 0x8A);
            Color yellow = Color.FromRgb(0xE0, 0xC0, 0x60);
            Color red = Color.FromRgb(0xE0, 0x60, 0x5A);
            Color c = avg <= 4.0 ? Lerp(green, yellow, (avg - 1.0) / 3.0)
                                 : Lerp(yellow, red, (avg - 4.0) / 4.0);
            return new SolidColorBrush(c);
        }

        private static Color Lerp(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        // Pulls a bottom-docked panel up to compensate for the layout gap the
        // session's RenderTransform scaling leaves behind (it keeps its full
        // unscaled layout slot). Stays 0 when docked to the top.
        private readonly TranslateTransform _translate = new TranslateTransform(0, 0);
        private readonly TransformGroup _transform = new TransformGroup();

        // Rows that have history, with their pre-built tooltip lines. Rebuilt on
        // every ShowRows/ShowTeams; polled each tick by UpdateHover to drive the
        // manual hover box (WPF ToolTips don't fire in HDT's click-through overlay).
        private readonly List<(FrameworkElement El, string Name, IReadOnlyList<string> Lines, int Seen)> _hoverRows =
            new List<(FrameworkElement, string, IReadOnlyList<string>, int)>();
        private FrameworkElement _hoverEl;

        // Rows that have a wallii identity, for the click-to-open dossier. Rebuilt on
        // every ShowRows/ShowTeams; hit-tested against the OS cursor on a fresh click.
        private readonly List<(FrameworkElement El, int PlayerId, string Region, string Name)> _clickRows =
            new List<(FrameworkElement, int, string, string)>();
        // The row whose dossier is currently open (null = none), for toggle + reposition.
        private FrameworkElement _dossierEl;

        /// <summary>Raised when a player row with a wallii identity is clicked. The
        /// engine fetches the dossier and calls back <see cref="ShowDossier"/>.</summary>
        public event Action<int, string, string, FrameworkElement> DossierRequested;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);
        // Low bit set = the key was pressed since the previous call — robust to our slow
        // (~1s) poll cadence: it reports "a click happened" regardless of timing.
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private const int VK_LBUTTON = 0x01;

        public LobbyMmrPanel()
        {
            InitializeComponent();
            _transform.Children.Add(_scale);
            _transform.Children.Add(_translate);
            Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Scale via RenderTransform anchored to the edge that touches the session
        /// window, so the panel stays flush at any scale (layout unchanged):
        /// bottom-left (0,1) when docked to the top, top-left (0,0) when docked to
        /// the bottom (combined with the upward translate set in <see cref="SetScale"/>).
        /// </summary>
        public void SetDockedAppearance(DockSide side)
        {
            RootBorder.RenderTransformOrigin =
                side == DockSide.Top ? new Point(0, 1) : new Point(0, 0);
            RootBorder.RenderTransform = _transform;
        }

        /// <param name="ratio">Session scale factor (1.0 = 100%).</param>
        /// <param name="offsetY">Vertical pixels to shift the panel (negative = up);
        /// non-zero only for bottom docking, to close the scaling gap.</param>
        public void SetScale(double ratio, double offsetY)
        {
            _scale.ScaleX = ratio;
            _scale.ScaleY = ratio;
            _translate.Y = offsetY;
        }

        // ---- Content -------------------------------------------------------

        /// <summary>Show a status message (loading / error / idle) and clear the rows.</summary>
        public void ShowMessage(string text)
        {
            ResetHover();
            RowsPanel.Children.Clear();
            StatusText.Text = text;
            StatusText.Visibility = Visibility.Visible;
            Visibility = Visibility.Visible;
        }

        /// <summary>Render the lobby player list.</summary>
        public void ShowRows(IReadOnlyList<PlayerRow> rows)
        {
            ResetHover();
            RowsPanel.Children.Clear();
            StatusText.Visibility = Visibility.Collapsed;

            foreach (PlayerRow row in rows)
                RowsPanel.Children.Add(BuildRow(row));

            Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Render duo lobby rows grouped by teammate pair. Each row still shows its
        /// own individual MMR/rank (no averaging); a thin divider and small label
        /// separate teams. Teams are pre-sorted by the caller.
        /// </summary>
        public void ShowTeams(IReadOnlyList<(int TeamNumber, bool HasSelf, List<PlayerRow> Rows)> teams)
        {
            ResetHover();
            RowsPanel.Children.Clear();
            StatusText.Visibility = Visibility.Collapsed;

            bool first = true;
            foreach (var team in teams)
            {
                if (!first)
                {
                    RowsPanel.Children.Add(new Border
                    {
                        BorderBrush = DividerBrush,
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        Margin = new Thickness(8, 6, 8, 0)
                    });
                }
                first = false;

                bool teamOut = team.Rows.Count > 0 && team.Rows.All(r => r.IsEliminated);
                RowsPanel.Children.Add(new TextBlock
                {
                    Text = team.HasSelf ? $"Team {team.TeamNumber} (you)" : $"Team {team.TeamNumber}",
                    FontSize = 10,
                    Foreground = teamOut ? EliminatedBrush : TeamLabelBrush,
                    Margin = new Thickness(10, 6, 8, 2)
                });

                foreach (PlayerRow row in team.Rows)
                    RowsPanel.Children.Add(BuildRow(row));
            }

            Visibility = Visibility.Visible;
        }

        private Border BuildRow(PlayerRow row)
        {
            var grid = new Grid { Margin = new Thickness(8, 2, 8, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // rank
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name (+ streamer dot)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // avg placement chip
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // mmr

            // HearthstoneTextBlock = HDT's outlined Belwe font control, same as the
            // session window. It uses Fill (not Foreground) for color.
            Brush rankFill = row.IsEliminated ? EliminatedBrush : (row.IsSelf ? SelfBrush : RankBrush);
            Brush nameFill = row.IsEliminated ? EliminatedBrush : (row.IsSelf ? SelfBrush : NameBrush);
            Brush mmrFill = row.IsEliminated ? EliminatedBrush : (row.IsSelf ? SelfBrush : MmrBrush);

            var rank = new HearthstoneTextBlock
            {
                Text = row.Rank,
                Fill = rankFill,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(rank, 0);

            var name = new HearthstoneTextBlock
            {
                Text = row.Name,
                Fill = nameFill,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var nameGroup = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
            nameGroup.Children.Add(name);
            if (row.StreamUrl != null)
            {
                // Live streamers get a bright red dot; offline keep the muted purple
                // "known streamer" marker.
                nameGroup.Children.Add(new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = row.IsLive ? LiveBrush : StreamerBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0),
                    ToolTip = (row.IsLive ? "LIVE now: " : "Known streamer: ") + row.StreamUrl
                });
            }
            Grid.SetColumn(nameGroup, 1);

            // Recent-form average placement (wallii.gg), colored best→worst. Greyed
            // out with the rest of the row once the player is eliminated.
            if (row.Avg.HasValue)
            {
                var avg = new TextBlock
                {
                    Text = row.Avg.Value.ToString("0.0"),
                    Foreground = row.IsEliminated ? EliminatedBrush : AvgBrush(row.Avg.Value),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = "Recent average placement (click for detail)"
                };
                Grid.SetColumn(avg, 2);
                grid.Children.Add(avg);
            }

            var mmr = new HearthstoneTextBlock
            {
                Text = row.Mmr,
                Fill = mmrFill,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(mmr, 3);

            grid.Children.Add(rank);
            grid.Children.Add(nameGroup);
            grid.Children.Add(mmr);

            var border = new Border
            {
                Background = (row.IsSelf && !row.IsEliminated) ? SelfRowBg : Brushes.Transparent,
                Child = grid
            };
            // Register every row; a player with no matched history shows a
            // "No history" box rather than nothing, so hovering always responds.
            _hoverRows.Add((border, row.Name, row.History, row.SeenCount));
            // Only wallii-tracked players get a clickable dossier.
            if (row.WalliiPlayerId > 0)
                _clickRows.Add((border, row.WalliiPlayerId, row.WalliiRegion, row.Name));
            return border;
        }

        // ---- Manual hover box (mouse polling) ------------------------------

        /// <summary>
        /// Poll the cursor and show the past-season box under whichever player row
        /// it's over. Called every update tick by the plugin. HDT's overlay is
        /// click-through so WPF never delivers mouse events here — we hit-test the
        /// row rectangles against the OS cursor position in screen pixels instead.
        /// </summary>
        public void UpdateHover()
        {
            if (!GetCursorPos(out POINT p))
            {
                HideHover();
                return;
            }

            HandleClick(p);

            if (_hoverRows.Count == 0)
            {
                HideHover();
                return;
            }

            foreach (var (el, name, lines, seen) in _hoverRows)
            {
                if (el.ActualWidth <= 0 || !el.IsVisible)
                    continue;
                Point tl = el.PointToScreen(new Point(0, 0));
                Point br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
                if (p.X >= tl.X && p.X <= br.X && p.Y >= tl.Y && p.Y <= br.Y)
                {
                    ShowHoverFor(el, name, lines, seen);
                    return;
                }
            }
            HideHover();
        }

        private void ShowHoverFor(FrameworkElement row, string name, IReadOnlyList<string> lines, int seen)
        {
            if (ReferenceEquals(_hoverEl, row))
                return; // already showing this row's box; nothing to rebuild

            HoverLines.Children.Clear();
            HoverLines.Children.Add(new TextBlock
            {
                Text = seen > 1 ? $"Played together {seen}x" : "First time vs this player",
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC2, 0xC2)),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4)
            });
            HoverLines.Children.Add(new TextBlock
            {
                Text = $"Past seasons - {name}",
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x94, 0x94)),
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 3)
            });
            if (lines == null || lines.Count == 0)
            {
                HoverLines.Children.Add(new TextBlock
                {
                    Text = "No history",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x94, 0x94)),
                    FontSize = 11
                });
            }
            else
            {
                foreach (string line in lines)
                    HoverLines.Children.Add(new TextBlock
                    {
                        Text = line,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE3, 0xE3)),
                        FontSize = 11
                    });
            }

            // Place the box just below the hovered row, in ContentRoot space.
            Point pos = row.TransformToAncestor(ContentRoot).Transform(new Point(0, row.ActualHeight));
            HoverTranslate.Y = pos.Y;
            HoverBox.Visibility = Visibility.Visible;
            _hoverEl = row;
        }

        private void HideHover()
        {
            if (_hoverEl == null)
                return;
            HoverBox.Visibility = Visibility.Collapsed;
            _hoverEl = null;
        }

        /// <summary>Drop stale row references and hide the boxes before a rebuild.</summary>
        private void ResetHover()
        {
            _hoverRows.Clear();
            _clickRows.Clear();
            HideHover();
            HideDossier();
        }

        // ---- Click dossier (mouse polling) ---------------------------------

        /// <summary>
        /// On a fresh left click, open the dossier for the row under the cursor (or
        /// toggle it shut if it's the same row); a click anywhere else closes it.
        /// </summary>
        private void HandleClick(POINT p)
        {
            if ((GetAsyncKeyState(VK_LBUTTON) & 0x0001) == 0)
                return; // no click since the last poll

            foreach (var (el, playerId, region, name) in _clickRows)
            {
                if (el.ActualWidth <= 0 || !el.IsVisible)
                    continue;
                Point tl = el.PointToScreen(new Point(0, 0));
                Point br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
                if (p.X >= tl.X && p.X <= br.X && p.Y >= tl.Y && p.Y <= br.Y)
                {
                    if (ReferenceEquals(_dossierEl, el))
                        HideDossier();          // second click on the open row closes it
                    else
                        DossierRequested?.Invoke(playerId, region, name, el);
                    return;
                }
            }
            HideDossier(); // clicked outside every dossier row
        }

        /// <summary>Show the dossier box with a loading placeholder under the row.</summary>
        public void ShowDossierLoading(string name, FrameworkElement row)
        {
            _dossierEl = row;
            DossierContent.Children.Clear();
            DossierContent.Children.Add(DossierHeader(name));
            DossierContent.Children.Add(new TextBlock
            {
                Text = "Loading…",
                Foreground = SubtleBrush,
                FontSize = 11
            });
            PositionDossier(row);
        }

        /// <summary>Fill the dossier with recent games + a rating sparkline. Ignored if
        /// the box was closed or moved to another row while the fetch was in flight.</summary>
        public void ShowDossier(string name, WalliiDossier dossier, FrameworkElement row)
        {
            if (!ReferenceEquals(_dossierEl, row))
                return; // stale result

            DossierContent.Children.Clear();
            DossierContent.Children.Add(DossierHeader(name));

            if (dossier == null || dossier.Games.Count == 0)
            {
                DossierContent.Children.Add(new TextBlock
                {
                    Text = "No recent games",
                    Foreground = SubtleBrush,
                    FontSize = 11
                });
                PositionDossier(row);
                return;
            }

            DossierContent.Children.Add(new TextBlock
            {
                Text = $"Last 7d: {Fmt(dossier.WeekAvg)} avg  ({dossier.WeekGames})" +
                       $"    Today: {Fmt(dossier.DayAvg)} ({dossier.DayGames})",
                Foreground = SubtleBrush,
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 2)
            });

            DossierContent.Children.Add(BuildSparkline(dossier.RatingHistory));

            foreach (GameRecord g in dossier.Games.Take(8))
                DossierContent.Children.Add(BuildGameLine(g));

            PositionDossier(row);
        }

        private static string Fmt(double? avg) => avg.HasValue ? avg.Value.ToString("0.0") : "-";

        private TextBlock DossierHeader(string name) => new TextBlock
        {
            Text = $"Recent games - {name}",
            Foreground = SubtleBrush,
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 3)
        };

        private UIElement BuildGameLine(GameRecord g)
        {
            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var ago = new TextBlock { Text = Ago(g.At), Foreground = SubtleBrush, FontSize = 11 };
            Grid.SetColumn(ago, 0);

            var place = new TextBlock
            {
                Text = "#" + g.Placement.ToString("0.#"),
                Foreground = AvgBrush(g.Placement),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(place, 1);

            var delta = new TextBlock
            {
                Text = (g.Delta >= 0 ? "+" : "") + g.Delta,
                Foreground = g.Delta >= 0
                    ? new SolidColorBrush(Color.FromRgb(0x5F, 0xD0, 0x8A))
                    : new SolidColorBrush(Color.FromRgb(0xE0, 0x60, 0x5A)),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(delta, 2);

            grid.Children.Add(ago);
            grid.Children.Add(place);
            grid.Children.Add(delta);
            return grid;
        }

        private UIElement BuildSparkline(IReadOnlyList<int> ratings)
        {
            const double W = 184, H = 34;
            var canvas = new Canvas { Width = W, Height = H, Margin = new Thickness(0, 2, 0, 4) };
            if (ratings == null || ratings.Count < 2)
                return canvas;
            int min = ratings.Min(), max = ratings.Max();
            double range = Math.Max(1, max - min);
            var pts = new PointCollection();
            for (int i = 0; i < ratings.Count; i++)
            {
                double x = W * i / (ratings.Count - 1);
                double y = H - 1 - (H - 2) * (ratings[i] - min) / range;
                pts.Add(new Point(x, y));
            }
            canvas.Children.Add(new Polyline { Points = pts, Stroke = SparkBrush, StrokeThickness = 1.5 });
            return canvas;
        }

        private static string Ago(DateTimeOffset at)
        {
            TimeSpan d = DateTimeOffset.UtcNow - at;
            if (d.TotalMinutes < 60) return $"{Math.Max(0, (int)d.TotalMinutes)}m";
            if (d.TotalHours < 24) return $"{(int)d.TotalHours}h";
            return $"{(int)d.TotalDays}d";
        }

        private void PositionDossier(FrameworkElement row)
        {
            Point pos = row.TransformToAncestor(ContentRoot).Transform(new Point(0, row.ActualHeight));
            DossierTranslate.Y = pos.Y;
            DossierBox.Visibility = Visibility.Visible;
        }

        private void HideDossier()
        {
            if (_dossierEl == null)
                return;
            DossierBox.Visibility = Visibility.Collapsed;
            _dossierEl = null;
        }
    }
}
