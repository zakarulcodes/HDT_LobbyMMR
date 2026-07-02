using System.Collections.Generic;
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
        /// <summary>Twitch/YouTube channel URL if this player is a known streamer, else null.</summary>
        public string StreamUrl;

        public PlayerRow(string name, string mmr, string rank, bool isSelf, string streamUrl = null)
        {
            Name = name;
            Mmr = mmr;
            Rank = rank;
            IsSelf = isSelf;
            StreamUrl = streamUrl;
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
        // Twitch brand purple, used as a plain "known streamer" marker dot.
        private static readonly Brush StreamerBrush = new SolidColorBrush(Color.FromRgb(0x91, 0x46, 0xFF));

        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        // Pulls a bottom-docked panel up to compensate for the layout gap the
        // session's RenderTransform scaling leaves behind (it keeps its full
        // unscaled layout slot). Stays 0 when docked to the top.
        private readonly TranslateTransform _translate = new TranslateTransform(0, 0);
        private readonly TransformGroup _transform = new TransformGroup();

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
            RowsPanel.Children.Clear();
            StatusText.Text = text;
            StatusText.Visibility = Visibility.Visible;
            Visibility = Visibility.Visible;
        }

        /// <summary>Render the lobby player list.</summary>
        public void ShowRows(IReadOnlyList<PlayerRow> rows)
        {
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

                RowsPanel.Children.Add(new TextBlock
                {
                    Text = team.HasSelf ? $"Team {team.TeamNumber} (you)" : $"Team {team.TeamNumber}",
                    FontSize = 10,
                    Foreground = TeamLabelBrush,
                    Margin = new Thickness(10, 6, 8, 2)
                });

                foreach (PlayerRow row in team.Rows)
                    RowsPanel.Children.Add(BuildRow(row));
            }

            Visibility = Visibility.Visible;
        }

        private static Border BuildRow(PlayerRow row)
        {
            var grid = new Grid { Margin = new Thickness(8, 2, 8, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // rank
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // streamer dot
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // mmr

            // HearthstoneTextBlock = HDT's outlined Belwe font control, same as the
            // session window. It uses Fill (not Foreground) for color.
            var rank = new HearthstoneTextBlock
            {
                Text = row.Rank,
                Fill = row.IsSelf ? SelfBrush : RankBrush,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(rank, 0);

            var name = new HearthstoneTextBlock
            {
                Text = row.Name,
                Fill = row.IsSelf ? SelfBrush : NameBrush,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(name, 1);

            var mmr = new HearthstoneTextBlock
            {
                Text = row.Mmr,
                Fill = row.IsSelf ? SelfBrush : MmrBrush,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(mmr, 3);

            grid.Children.Add(rank);
            grid.Children.Add(name);
            grid.Children.Add(mmr);

            if (row.StreamUrl != null)
            {
                var streamerDot = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = StreamerBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    ToolTip = $"Known streamer: {row.StreamUrl}"
                };
                Grid.SetColumn(streamerDot, 2);
                grid.Children.Add(streamerDot);
            }

            return new Border
            {
                Background = row.IsSelf ? SelfRowBg : Brushes.Transparent,
                Child = grid
            };
        }
    }
}
