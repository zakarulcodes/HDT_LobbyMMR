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

        public PlayerRow(string name, string mmr, string rank, bool isSelf, bool isEliminated,
            string streamUrl = null, IReadOnlyList<string> history = null)
        {
            Name = name;
            Mmr = mmr;
            Rank = rank;
            IsSelf = isSelf;
            IsEliminated = isEliminated;
            StreamUrl = streamUrl;
            History = history;
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
        // "Known streamer" marker dot.
        private static readonly Brush StreamerBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A));
        // Muted grey for eliminated players (and a fully-eliminated duo team header).
        private static readonly Brush EliminatedBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0x72, 0x75));

        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        // Pulls a bottom-docked panel up to compensate for the layout gap the
        // session's RenderTransform scaling leaves behind (it keeps its full
        // unscaled layout slot). Stays 0 when docked to the top.
        private readonly TranslateTransform _translate = new TranslateTransform(0, 0);
        private readonly TransformGroup _transform = new TransformGroup();

        // Rows that have history, with their pre-built tooltip lines. Rebuilt on
        // every ShowRows/ShowTeams; polled each tick by UpdateHover to drive the
        // manual hover box (WPF ToolTips don't fire in HDT's click-through overlay).
        private readonly List<(FrameworkElement El, string Name, IReadOnlyList<string> Lines)> _hoverRows =
            new List<(FrameworkElement, string, IReadOnlyList<string>)>();
        private FrameworkElement _hoverEl;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

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
                nameGroup.Children.Add(new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = StreamerBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0),
                    ToolTip = $"Known streamer: {row.StreamUrl}"
                });
            }
            Grid.SetColumn(nameGroup, 1);

            var mmr = new HearthstoneTextBlock
            {
                Text = row.Mmr,
                Fill = mmrFill,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(mmr, 2);

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
            _hoverRows.Add((border, row.Name, row.History));
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
            if (_hoverRows.Count == 0 || !GetCursorPos(out POINT p))
            {
                HideHover();
                return;
            }

            foreach (var (el, name, lines) in _hoverRows)
            {
                if (el.ActualWidth <= 0 || !el.IsVisible)
                    continue;
                Point tl = el.PointToScreen(new Point(0, 0));
                Point br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
                if (p.X >= tl.X && p.X <= br.X && p.Y >= tl.Y && p.Y <= br.Y)
                {
                    ShowHoverFor(el, name, lines);
                    return;
                }
            }
            HideHover();
        }

        private void ShowHoverFor(FrameworkElement row, string name, IReadOnlyList<string> lines)
        {
            if (ReferenceEquals(_hoverEl, row))
                return; // already showing this row's box; nothing to rebuild

            HoverLines.Children.Clear();
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

        /// <summary>Drop stale row references and hide the box before a rebuild.</summary>
        private void ResetHover()
        {
            _hoverRows.Clear();
            HideHover();
        }
    }
}
