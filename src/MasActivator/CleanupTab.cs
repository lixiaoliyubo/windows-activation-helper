using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MasActivator
{
    /// <summary>
    /// 「垃圾清理」标签页的界面与执行逻辑：
    /// 分组卡片渲染、并发扫描、并发清理、实时进度与当前路径提示。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>并发扫描的分组界面对象。</summary>
        private sealed class GroupUi
        {
            public string Category;
            public readonly List<ItemUi> Items = new List<ItemUi>();
            public TextBlock Status;
            public TextBlock Chevron;
            public FrameworkElement Body;
            public bool Expanded;
        }

        /// <summary>单条清理项对应的界面对象。</summary>
        private sealed class ItemUi
        {
            public CleanupItem Item;
            public CheckBox Check;
            public TextBlock Size;
            public Border Root;
            public StackPanel Container;
            public StackPanel DetailHost;
            public TextBlock Toggle;
            public bool Expanded;
            public bool DetailsBuilt;
            public readonly List<DetailUi> Details = new List<DetailUi>();
        }

        /// <summary>明细行的界面对象。</summary>
        private sealed class DetailUi
        {
            public CleanupDetail Detail;
            public CheckBox Check;
            public TextBlock Size;
        }

        private readonly List<GroupUi> _groups = new List<GroupUi>();
        private bool _hasScanned;
        private bool _isScanning;
        private bool _isCleaning;
        private bool _suppressCheck;
        private CancellationTokenSource _cts;
        private DispatcherTimer _ticker;
        private int _totalWork;
        private int _doneWork;
        private long _discoveredBytes;
        private long _cleanedBytes;
        private int _failedCount;
        private volatile string _currentPath = "";
        private int _scanConcurrency = 4;
        private int _runGen;
        private bool _cleanDone;
        private int _cleanConcurrency = 3;

        private static readonly string[] CategoryOrder =
        {
            DiskCleaner.CatSystem, DiskCleaner.CatUpdate, DiskCleaner.CatApp,
            DiskCleaner.CatTrail, DiskCleaner.CatRisk
        };

        // ------------------------------------------------------------------
        // 界面构建
        // ------------------------------------------------------------------

        /// <summary>环形进度半径（112px 画板内、贴着 72px 圆形图标外侧）。</summary>
        private const double RingRadius = 41;

        /// <summary>开始扫描/清理时的视觉状态：进度弧 + 底轨 + 由内向外的呼吸光晕，扫描红 / 清理橙。</summary>
        private void StartScanVisual(bool cleaning)
        {
            Color inner = cleaning ? Color.FromRgb(0xE0, 0x8A, 0x2E) : Color.FromRgb(0xD2, 0x36, 0x36);
            Color glow = cleaning ? Color.FromRgb(0xF5, 0x9E, 0x0B) : Color.FromRgb(0xF0, 0x5A, 0x52);
            Color track = cleaning ? Color.FromRgb(0xF6, 0xE3, 0xC9) : Color.FromRgb(0xF7, 0xDD, 0xDC);
            Color iconFg = cleaning ? Color.FromRgb(0xB4, 0x53, 0x09) : Color.FromRgb(0xC5, 0x30, 0x30);
            Color iconBg = cleaning ? Color.FromRgb(0xFD, 0xF1, 0xDF) : Color.FromRgb(0xFD, 0xEA, 0xEA);

            ScanRing.Stroke = new SolidColorBrush(inner);
            ScanRing.Visibility = Visibility.Visible;
            ScanRing.Opacity = 1;
            RingTrack.Stroke = new SolidColorBrush(track);
            RingTrack.Visibility = Visibility.Visible;

            HeroIconGlyph.Foreground = new SolidColorBrush(iconFg);
            HeroIconCircle.Background = new SolidColorBrush(iconBg);
            HeroIconGlyph.Visibility = Visibility.Collapsed;
            HeroPercent.Foreground = new SolidColorBrush(iconFg);
            HeroPercent.Text = "0%";
            HeroPercent.Visibility = Visibility.Visible;

            ScanGlow.Fill = BuildGlowBrush(glow);
            ScanGlow.Visibility = Visibility.Visible;
            ScanGlow.Opacity = 1;

            // 进度弧呼吸闪烁
            var breath = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            ScanRing.BeginAnimation(UIElement.OpacityProperty, breath);

            // 光晕：由内向外呼吸 + 轻微放大
            var halo = new DoubleAnimation(0.30, 1.0, TimeSpan.FromMilliseconds(1300))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            ScanGlow.BeginAnimation(UIElement.OpacityProperty, halo);

            ScaleTransform glowScale = ScanGlow.RenderTransform as ScaleTransform;
            if (glowScale == null)
            {
                glowScale = new ScaleTransform(1, 1);
                ScanGlow.RenderTransform = glowScale;
                ScanGlow.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            var pulse = new DoubleAnimation(0.97, 1.05, TimeSpan.FromMilliseconds(1300))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            glowScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            glowScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);

            UpdateRing(0);
        }

        /// <summary>由内向外渐隐的径向光晕笔刷（图标边缘最亮，向外衰减到透明）。</summary>
        private static RadialGradientBrush BuildGlowBrush(Color color)
        {
            var brush = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x10, color.R, color.G, color.B), 0.62));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x44, color.R, color.G, color.B), 0.74));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xD2, color.R, color.G, color.B), 0.84));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, color.R, color.G, color.B), 0.92));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0));
            return brush;
        }

        /// <summary>停止并复位环形进度与其动画，图标恢复默认蓝色。</summary>
        private void StopScanVisual()
        {
            ScanRing.BeginAnimation(UIElement.OpacityProperty, null);
            ScanRing.Visibility = Visibility.Collapsed;
            ScanRing.Data = Geometry.Empty;
            ScanRing.Opacity = 1;
            RingTrack.Visibility = Visibility.Collapsed;

            HeroIconGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB0));
            HeroIconCircle.Background = new SolidColorBrush(Color.FromRgb(0xE7, 0xF0, 0xFB));
            HeroIconGlyph.Visibility = Visibility.Visible;
            HeroPercent.Visibility = Visibility.Collapsed;

            ScanGlow.BeginAnimation(UIElement.OpacityProperty, null);
            ScanGlow.Visibility = Visibility.Collapsed;
            ScanGlow.Opacity = 0;
            ScaleTransform glowScale = ScanGlow.RenderTransform as ScaleTransform;
            if (glowScale != null)
            {
                glowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                glowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                glowScale.ScaleX = 1;
                glowScale.ScaleY = 1;
            }
        }

        /// <summary>按百分比更新圆环弧长（0 与 100 分别用空/整圆），并在圆心里显示百分比。</summary>
        private void UpdateRing(double percent)
        {
            double p = Math.Max(0, Math.Min(100, percent));
            var center = new Point(56, 56);

            if (HeroPercent.Visibility == Visibility.Visible)
            {
                HeroPercent.Text = ((int)Math.Round(p)) + "%";
            }

            if (p <= 0.5)
            {
                ScanRing.Data = Geometry.Empty;
                return;
            }
            if (p >= 99.5)
            {
                ScanRing.Data = new EllipseGeometry(center, RingRadius, RingRadius);
                return;
            }

            double sweep = p / 100.0 * 360.0;
            double rad = (sweep - 90) * Math.PI / 180;
            var start = new Point(center.X, center.Y - RingRadius);
            var end = new Point(center.X + RingRadius * Math.Cos(rad), center.Y + RingRadius * Math.Sin(rad));

            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment(end, new Size(RingRadius, RingRadius), 0,
                sweep > 180, SweepDirection.Clockwise, true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            ScanRing.Data = geometry;
        }

        /// <summary>构建分组卡片列表。</summary>
        private void BuildCleanItems()
        {
            _groups.Clear();
            CleanGroupsPanel.Children.Clear();

            List<CleanupItem> items = DiskCleaner.BuildItems();
            foreach (string cat in CategoryOrder)
            {
                var groupItems = items.Where(i => i.Category == cat).ToList();
                if (groupItems.Count == 0) continue;
                _groups.Add(BuildGroupCard(cat, groupItems));
            }

            _hasScanned = false;
            RefreshAll();
            BtnClean.IsEnabled = false;
            SetHeroValue(null);
            TxtHeroSub.Text = "点击右侧「开始扫描」，先统计可释放空间";
            ScanProgress.Visibility = Visibility.Collapsed;
            ScanProgress.Value = 0;
        }

        private GroupUi BuildGroupCard(string category, List<CleanupItem> items)
        {
            var group = new GroupUi { Category = category };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            group.Chevron = new TextBlock
            {
                Text = "\uE70E",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                Foreground = Brush("#55617A"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(group.Chevron, "分组:" + category);

            var chevBox = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(7),
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "展开 / 收起分组",
                Child = group.Chevron
            };
            AutomationProperties.SetName(chevBox, "分组按钮:" + category);
            chevBox.MouseEnter += (s, e) => chevBox.Background = Brush("#E7EEF9");
            chevBox.MouseLeave += (s, e) => chevBox.Background = Brushes.Transparent;
            chevBox.MouseLeftButtonUp += (s, e) => ToggleGroup(group);
            head.Children.Add(chevBox);

            var bar = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(CategoryColor(category)),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var title = new TextBlock
            {
                Text = category,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#1F2937"),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };
            title.MouseLeftButtonUp += (s, e) => ToggleGroup(group);

            var headLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            headLeft.Children.Add(bar);
            headLeft.Children.Add(title);
            Grid.SetColumn(headLeft, 1);
            head.Children.Add(headLeft);

            group.Status = new TextBlock
            {
                Text = "等待扫描",
                FontSize = 12,
                Foreground = Brush("#8A94A6"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            group.Status.MouseLeftButtonUp += (s, e) => ToggleGroup(group);
            Grid.SetColumn(group.Status, 2);
            head.Children.Add(group.Status);

            var ops = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var all = new Button { Content = "全选", Style = (Style)FindResource("LinkButton"), FontSize = 12, Margin = new Thickness(0, 0, 2, 0) };
            all.Click += (s, e) => SetGroupChecked(group, true);
            var none = new Button { Content = "全不选", Style = (Style)FindResource("LinkButton"), FontSize = 12 };
            none.Click += (s, e) => SetGroupChecked(group, false);
            ops.Children.Add(all);
            ops.Children.Add(none);
            Grid.SetColumn(ops, 4);
            head.Children.Add(ops);

            var itemStack = new StackPanel { Margin = new Thickness(32, 8, 0, 4) };
            foreach (CleanupItem item in items)
            {
                ItemUi row = BuildItemRow(item);
                group.Items.Add(row);
                itemStack.Children.Add(row.Container);
            }

            // 树形引导线：从组头下方垂到最后一行的左侧
            var guide = new Border
            {
                Width = 1,
                Background = Brush("#E6EDF7"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(22, 4, 0, 10)
            };

            var body = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            body.Children.Add(guide);
            body.Children.Add(itemStack);
            group.Body = body;

            bool expanded = CleanGroupsPanel.Children.Count == 0;
            group.Expanded = expanded;
            body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            group.Chevron.Text = expanded ? "\uE70E" : "\uE70D";

            var content = new StackPanel();
            content.Children.Add(head);
            content.Children.Add(body);

            // 列表式：分组之间只画一条分隔线，不再各自成卡片
            if (CleanGroupsPanel.Children.Count > 0)
            {
                CleanGroupsPanel.Children.Add(new Border
                {
                    Height = 1,
                    Background = Brush("#EDF1F7"),
                    Margin = new Thickness(0, 0, 0, 0)
                });
            }

            var section = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(16, 10, 14, 6),
                Child = content
            };
            head.Background = Brushes.Transparent;
            head.Cursor = Cursors.Hand;
            head.MouseLeftButtonUp += (s, e) =>
            {
                if (IsInsideControl(e.OriginalSource, head)) return;
                ToggleGroup(group);
            };
            CleanGroupsPanel.Children.Add(section);
            return group;
        }

        /// <summary>展开 / 收起分组；收起时组头仍显示「共 X，已选中 Y」。</summary>
        private void ToggleGroup(GroupUi group)
        {
            group.Expanded = !group.Expanded;
            group.Body.Visibility = group.Expanded ? Visibility.Visible : Visibility.Collapsed;
            group.Chevron.Text = group.Expanded ? "\uE70E" : "\uE70D";
        }

        /// <summary>构建一行清理项：勾选框 + 谨慎标记 + 右侧体量 + 明细展开箭头。</summary>
        private ItemUi BuildItemRow(CleanupItem item)
        {
            var row = new ItemUi { Item = item };

            var grid = new Grid { Margin = new Thickness(0), MinHeight = 32 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Check = new CheckBox
            {
                Style = (Style)FindResource("ItemCheckBox"),
                IsChecked = item.DefaultChecked,
                Content = item.Title,
                FontSize = 13,
                ToolTip = item.Hint
            };
            row.Check.Checked += (s, e) => OnItemCheckChanged(row);
            row.Check.Unchecked += (s, e) => OnItemCheckChanged(row);
            left.Children.Add(row.Check);

            if (item.Cautious)
            {
                var tag = new Border
                {
                    Background = Brush("#FDF0ED"),
                    BorderBrush = Brush("#F7CFC7"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 0, 5, 0),
                    Margin = new Thickness(7, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "谨慎清理", FontSize = 10, Foreground = Brush("#C0392B") }
                };
                left.Children.Add(tag);
            }
            grid.Children.Add(left);

            row.Size = new TextBlock
            {
                Text = "",
                FontSize = 12.5,
                Foreground = Brush("#94A3B8"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(row.Size, 1);
            grid.Children.Add(row.Size);

            row.Toggle = new TextBlock
            {
                Text = "\uE70D",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Foreground = Brush("#7A8698"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(row.Toggle, "明细:" + item.Title);

            var toggleBox = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(5),
                Background = Brushes.Transparent,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "点击整行可展开 / 收起明细",
                Child = row.Toggle
            };
            AutomationProperties.SetName(toggleBox, "明细按钮:" + item.Title);
            Grid.SetColumn(toggleBox, 2);
            grid.Children.Add(toggleBox);

            var wrap = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "点击整行可展开 / 收起明细",
                Child = grid
            };
            wrap.MouseEnter += (s, e) => wrap.Background = Brush("#F3F7FC");
            wrap.MouseLeave += (s, e) => wrap.Background = Brushes.Transparent;
            wrap.MouseLeftButtonUp += (s, e) =>
            {
                if (IsInsideControl(e.OriginalSource, wrap)) return;
                ToggleDetails(row);
            };
            row.Root = wrap;

            row.DetailHost = new StackPanel { Margin = new Thickness(0, 1, 0, 3), Visibility = Visibility.Collapsed };
            row.Container = new StackPanel();
            row.Container.Children.Add(wrap);
            row.Container.Children.Add(row.DetailHost);
            return row;
        }

        /// <summary>判断点击源是否落在按钮 / 复选框上（这些控件自己处理点击，不能顺带展开行）。</summary>
        private static bool IsInsideControl(object source, DependencyObject stopAt)
        {
            DependencyObject d = source as DependencyObject;
            while (d != null && d != stopAt)
            {
                if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        /// <summary>展开 / 收起某项的扫描明细。</summary>
        private void ToggleDetails(ItemUi row)
        {
            row.Expanded = !row.Expanded;
            row.Toggle.Text = row.Expanded ? "\uE70E" : "\uE70D";
            row.Toggle.Foreground = row.Expanded ? Brush("#2B6CB0") : Brush("#B6C2D2");
            row.DetailHost.Visibility = row.Expanded ? Visibility.Visible : Visibility.Collapsed;
            if (row.Expanded && !row.DetailsBuilt) BuildDetails(row);
        }

        /// <summary>按扫描结果生成明细行：每条目录 / 文件 / 注册表键一行，可单独勾选。</summary>
        private void BuildDetails(ItemUi row)
        {
            row.DetailHost.Children.Clear();
            row.Details.Clear();
            row.DetailsBuilt = true;

            if (!row.Item.Scanned)
            {
                row.DetailHost.Children.Add(new TextBlock
                {
                    Text = "扫描后显示具体的目录与大小",
                    FontSize = 12,
                    Foreground = Brush("#94A3B8"),
                    Margin = new Thickness(20, 3, 0, 5)
                });
                return;
            }

            if (row.Item.Details.Count == 0)
            {
                row.DetailHost.Children.Add(new TextBlock
                {
                    Text = "没有找到可清理的具体内容",
                    FontSize = 12,
                    Foreground = Brush("#94A3B8"),
                    Margin = new Thickness(20, 3, 0, 5)
                });
                return;
            }

            foreach (CleanupDetail detail in row.Item.Details)
            {
                var line = new Grid { Margin = new Thickness(20, 2, 0, 2), MinHeight = 32 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var cb = new CheckBox
                {
                    Style = (Style)FindResource("ItemCheckBox"),
                    IsChecked = detail.Checked,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var d = new DetailUi { Detail = detail, Check = cb };
                string shown = detail.Path == "RecycleBin" ? "回收站中的全部文件" : detail.Path;
                AutomationProperties.SetName(cb, "明细项:" + shown);
                cb.Checked += (s, e) => OnDetailCheckChanged(row, d);
                cb.Unchecked += (s, e) => OnDetailCheckChanged(row, d);
                line.Children.Add(cb);

                var pathText = new TextBlock
                {
                    Text = shown,
                    FontSize = 12.5,
                    Foreground = Brush("#6B7280"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = shown + "\n右键：定位到该位置 / 复制路径"
                };
                Grid.SetColumn(pathText, 1);
                line.Children.Add(pathText);

                var size = new TextBlock
                {
                    Text = detail.SizeText,
                    FontSize = 12.5,
                    Foreground = Brush("#94A3B8"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(size, 2);
                line.Children.Add(size);

                d.Size = size;
                row.Details.Add(d);
                ContextMenu menu = BuildDetailMenu(detail, shown);
                line.Background = Brushes.Transparent;
                line.ContextMenu = menu;
                cb.ContextMenu = menu;
                pathText.ContextMenu = menu;
                size.ContextMenu = menu;
                row.DetailHost.Children.Add(line);
            }
        }

        /// <summary>
        /// 生成一条明细的右键菜单：可在资源管理器中定位该文件夹 / 文件，或复制路径。
        /// 菜单同时挂到整行、勾选框与文字上，右键落点在哪都能弹出。
        /// </summary>
        private ContextMenu BuildDetailMenu(CleanupDetail detail, string shown)
        {
            var menu = new ContextMenu { FontSize = 11.5 };
            try { menu.Style = (Style)FindResource("CleanMenu"); } catch { }

            if (detail.Path == "RecycleBin")
            {
                var open = new MenuItem { Header = "打开回收站" };
                open.Click += (s, e) => OpenExplorer("shell:RecycleBinFolder", false);
                menu.Items.Add(open);
            }
            else if (System.IO.Path.IsPathRooted(detail.Path))
            {
                string full = Environment.ExpandEnvironmentVariables(detail.Path);
                bool isFile = System.IO.File.Exists(full);
                bool exists = isFile || System.IO.Directory.Exists(full);
                var open = new MenuItem
                {
                    Header = isFile ? "定位该文件" : "打开该文件夹",
                    IsEnabled = exists
                };
                open.Click += (s, e) => OpenExplorer(full, isFile);
                menu.Items.Add(open);
            }
            else
            {
                var reg = new MenuItem { Header = "该路径为注册表位置，可复制后手动查看", IsEnabled = false };
                menu.Items.Add(reg);
            }

            var copy = new MenuItem { Header = "复制路径" };
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(detail.Path == "RecycleBin" ? "回收站" : shown); }
                catch { }
            };
            menu.Items.Add(copy);

            try
            {
                Style itemStyle = (Style)FindResource("CleanMenuItem");
                foreach (object o in menu.Items)
                {
                    if (o is MenuItem) ((MenuItem)o).Style = itemStyle;
                }
            }
            catch { }

            return menu;
        }

        /// <summary>用资源管理器打开目标：shell 路径、文件夹或选中文件。</summary>
        private static void OpenExplorer(string target, bool selectFile)
        {
            try
            {
                if (target.StartsWith("shell:"))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", target)
                    {
                        UseShellExecute = true
                    });
                }
                else if (selectFile)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "explorer.exe", "/select,\"" + target + "\"") { UseShellExecute = true });
                }
                else
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
                    {
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
                // 资源管理器启动失败不影响界面。
            }
        }

        private static Color CategoryColor(string category)
        {
            switch (category)
            {
                case DiskCleaner.CatSystem: return (Color)ColorConverter.ConvertFromString("#2B6CB0");
                case DiskCleaner.CatUpdate: return (Color)ColorConverter.ConvertFromString("#0E9F8A");
                case DiskCleaner.CatApp: return (Color)ColorConverter.ConvertFromString("#D97706");
                case DiskCleaner.CatTrail: return (Color)ColorConverter.ConvertFromString("#7A5AF8");
                default: return (Color)ColorConverter.ConvertFromString("#C0392B");
            }
        }

        private static SolidColorBrush Brush(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        // ------------------------------------------------------------------
        // 勾选与统计
        // ------------------------------------------------------------------

        /// <summary>勾选整项：同步到所有明细（已展开明细时）。</summary>
        private void OnItemCheckChanged(ItemUi row)
        {
            if (_suppressCheck) return;
            _cleanDone = false;
            bool value = row.Check.IsChecked == true;
            if (row.DetailsBuilt && row.Details.Count > 0)
            {
                _suppressCheck = true;
                foreach (DetailUi d in row.Details)
                {
                    d.Detail.Checked = value;
                    d.Check.IsChecked = value;
                }
                _suppressCheck = false;
            }
            UpdateSummary();
            UpdateGroupStatus();
        }

        /// <summary>勾选明细：反推整项勾选状态（任一条勾选即视为选中）。</summary>
        private void OnDetailCheckChanged(ItemUi row, DetailUi detail)
        {
            if (_suppressCheck) return;
            detail.Detail.Checked = detail.Check.IsChecked == true;
            _cleanDone = false;

            bool any = false;
            foreach (DetailUi d in row.Details) if (d.Detail.Checked) { any = true; break; }
            _suppressCheck = true;
            row.Check.IsChecked = any;
            _suppressCheck = false;

            UpdateSummary();
            UpdateGroupStatus();
        }

        /// <summary>清理选中判定：整项勾选且有可清理明细。</summary>
        private static bool InSelection(ItemUi r)
        {
            return r.Check.IsChecked == true && r.Item.AnySelected;
        }

        /// <summary>选中项实际会清理的字节数（按明细累加）。</summary>
        private static long SelectedBytes(ItemUi r)
        {
            return InSelection(r) ? r.Item.SelectedBytes : 0;
        }

        private IEnumerable<ItemUi> AllRows()
        {
            foreach (GroupUi g in _groups)
            {
                foreach (ItemUi r in g.Items) yield return r;
            }
        }

        private static bool IsChecked(ItemUi r)
        {
            return r.Check.IsChecked == true;
        }

        private void SetGroupChecked(GroupUi group, bool value)
        {
            _suppressCheck = true;
            foreach (ItemUi r in group.Items) r.Check.IsChecked = value;
            _suppressCheck = false;
            UpdateSummary();
            UpdateGroupStatus();
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isScanning || _isCleaning) return;
            _suppressCheck = true;
            foreach (ItemUi r in AllRows()) r.Check.IsChecked = true;
            _suppressCheck = false;
            UpdateSummary();
            UpdateGroupStatus();
        }

        private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
        {
            if (_isScanning || _isCleaning) return;
            _suppressCheck = true;
            foreach (ItemUi r in AllRows()) r.Check.IsChecked = false;
            _suppressCheck = false;
            UpdateSummary();
            UpdateGroupStatus();
        }

        private void BtnResetDefault_Click(object sender, RoutedEventArgs e)
        {
            if (_isScanning || _isCleaning) return;
            _suppressCheck = true;
            foreach (ItemUi r in AllRows()) r.Check.IsChecked = r.Item.DefaultChecked;
            _suppressCheck = false;
            UpdateSummary();
            UpdateGroupStatus();
        }

        /// <summary>刷新所有行体量、分组状态与底栏合计。</summary>
        private void RefreshAll()
        {
            foreach (ItemUi r in AllRows())
            {
                bool ok = r.Item.Scanned;
                r.Size.Text = ok ? r.Item.SizeText : "";
                r.Size.Foreground = ok
                    ? (IsChecked(r) ? Brush("#2B6CB0") : Brush("#6B7280"))
                    : Brush("#94A3B8");
            }
            UpdateGroupStatus();
            UpdateSummary();
        }

        private void UpdateGroupStatus()
        {
            foreach (GroupUi g in _groups)
            {
                long total = 0, selected = 0;
                bool anyScanned = false;
                foreach (ItemUi r in g.Items)
                {
                    if (!r.Item.Scanned) continue;
                    anyScanned = true;
                    total += r.Item.SizeBytes;
                    if (IsChecked(r)) selected += r.Item.SelectedBytes;
                }

                if (_isScanning && !anyScanned)
                {
                    g.Status.Text = "正在扫描…";
                    g.Status.Foreground = Brush("#2B6CB0");
                }
                else if (!anyScanned)
                {
                    g.Status.Text = "等待扫描";
                    g.Status.Foreground = Brush("#8A94A6");
                }
                else
                {
                    g.Status.Text = "共 " + DiskCleaner.FormatSize(total) + "，已选中 " + DiskCleaner.FormatSize(selected);
                    g.Status.Foreground = selected > 0 ? Brush("#2B6CB0") : Brush("#8A94A6");
                }
            }
        }

        /// <summary>把「12.34 GB」这类文本拆成"大号数字 + 小号单位"，空值显示占位符。</summary>
        private void SetHeroValue(string sizeText)
        {
            if (string.IsNullOrEmpty(sizeText))
            {
                TxtHeroSize.Text = "0";
                TxtHeroUnit.Text = "GB";
                return;
            }
            int sp = sizeText.LastIndexOf(' ');
            if (sp > 0)
            {
                TxtHeroSize.Text = sizeText.Substring(0, sp);
                TxtHeroUnit.Text = sizeText.Substring(sp + 1);
            }
            else
            {
                TxtHeroSize.Text = sizeText;
                TxtHeroUnit.Text = "";
            }
        }

        private void UpdateSummary()
        {
            int count = _hasScanned ? AllRows().Count(InSelection) : AllRows().Count(IsChecked);
            long bytes = _hasScanned ? AllRows().Sum(SelectedBytes) : 0;
            int details = _hasScanned
                ? AllRows().Where(r => InSelection(r) && r.Item.Details.Count > 0)
                    .Sum(r => r.Item.Details.Count(d => d.Checked))
                : 0;

            TxtCleanSummary.Text = details > 0
                ? "已选中 " + count + " 项 · " + details + " 条明细"
                : "已选中 " + count + " 项";

            TxtHeroSelected.Text = _hasScanned ? DiskCleaner.FormatSize(bytes) : (_isScanning ? "统计中…" : "待统计");
            TxtHeroSelectedMeta.Text = details > 0
                ? count + " 项 · " + details + " 条明细"
                : count + " 项";

            if (_isCleaning)
            {
                TxtCleanHint.Text = "正在清理，请稍候…";
                return;
            }
            if (_isScanning)
            {
                TxtCleanHint.Text = "正在扫描中，可随时取消";
            }
            else if (_hasScanned)
            {
                TxtCleanHint.Text = count == 0
                    ? "请至少勾选一项后再清理"
                    : "预计可释放 " + DiskCleaner.FormatSize(bytes) + "，清理前会再次确认";
            }
            else
            {
                TxtCleanHint.Text = _cleanDone ? "清理完成 · 重新扫描可查看剩余垃圾" : "扫描后可查看每一项的实际占用";
            }

            BtnClean.IsEnabled = _hasScanned && count > 0 && !_isScanning && !_isCleaning;
        }

        // ------------------------------------------------------------------
        // 正在扫描：同一按钮切换为「取消扫描」
        // ------------------------------------------------------------------

        private async void BtnScanClean_Click(object sender, RoutedEventArgs e)
        {
            Log("BtnScanClean_Click scanning=" + _isScanning + " cleaning=" + _isCleaning + " cts=" + (_cts != null));
            if (_isScanning || _isCleaning)
            {
                bool wasCleaning = _isCleaning;
                CancelRunning();
                if (wasCleaning) TxtHeroSub.Text = "已取消清理 · 已删除的部分不可恢复";
                Log("用户取消运行");
                return;
            }
            await RunScanAsync();
        }

        /// <summary>轻量调试日志，便于定位扫描/清理卡住的位置。</summary>
        private static void Log(string message)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cleaner_debug.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>
        /// 立即取消：先把界面复位成「未运行」状态，再通知后台停止。
        /// 后台可能仍在收尾（如回收站统计），其后续结果按代际号丢弃，不影响界面。
        /// </summary>
        private void CancelRunning()
        {
            _runGen++;
            _isScanning = false;
            _isCleaning = false;
            _hasScanned = false;
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
                _cts = null;
            }
            StopScanVisual();
            StopTicker();
            ScanProgress.Visibility = Visibility.Collapsed;
            BtnScanClean.Content = "开始扫描";
            BtnScanClean.Style = (Style)FindResource("AccentButton");
            BtnScanClean.IsEnabled = true;
            TxtHeroPrefix.Text = "已发现";
            TxtHeroUnitText.Text = "可清理垃圾";
            TxtHeroSub.Text = "已取消 · 已统计部分数据，可重新扫描";
            RefreshAll();
            UpdateSummary();
            BtnClean.IsEnabled = false;
        }

        private async Task RunScanAsync()
        {
            List<ItemUi> rows = AllRows().ToList();
            int gen = ++_runGen;
            _isScanning = true;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            foreach (ItemUi r in rows)
            {
                r.Item.Scanned = false;
                r.Item.SizeBytes = 0;
                r.Item.RecordCount = 0;
                r.Item.Details.Clear();
                r.DetailsBuilt = false;
                if (r.Expanded) BuildDetails(r);
            }

            _totalWork = rows.Count;
            _doneWork = 0;
            _discoveredBytes = 0;
            _currentPath = "准备扫描…";
            _hasScanned = false;

            RefreshAll();
            _cleanDone = false;
            TxtHeroPrefix.Text = "已发现";
            SetHeroValue("0 MB");
            TxtHeroUnitText.Text = "可清理垃圾";
            TxtHeroSub.Text = "正在扫描: 准备…";
            ScanProgress.Visibility = Visibility.Visible;
            ScanProgress.Value = 0;
            BtnScanClean.Content = "取消扫描";
            BtnScanClean.Style = (Style)FindResource("WarnButton");
            BtnClean.IsEnabled = false;
            StartTicker();
            StartScanVisual(false);
            CleanScroll.ScrollToTop();

            try
            {
                Log("开始扫描，共 " + rows.Count + " 项");
                await Task.Run(() =>
                {
                    var options = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = _scanConcurrency,
                        CancellationToken = token
                    };
                    Parallel.ForEach(rows, options, r =>
                    {
                        token.ThrowIfCancellationRequested();
                        Log("扫描项开始 " + r.Item.Title);
                        _currentPath = "正在扫描·" + r.Item.Title;
                        long size = DiskCleaner.ScanItem(r.Item, p => _currentPath = p, token);
                        r.Item.SizeBytes = size;
                        r.Item.Scanned = true;
                        Interlocked.Add(ref _discoveredBytes, size);
                        Interlocked.Increment(ref _doneWork);
                        Log("扫描项完成 " + r.Item.Title + " = " + size);
                    });
                }, token);
                Log("Parallel 正常结束");
                if (gen != _runGen) { Log("扫描结果已作废（已取消）"); return; }

                _isScanning = false;
                _hasScanned = true;
                UpdateRing(100);
                StopScanVisual();
                StopTicker();

                ScanProgress.Value = 100;
                ScanProgress.Visibility = Visibility.Collapsed;
                foreach (ItemUi r in rows)
                {
                    if (!r.Expanded) continue;
                    r.DetailsBuilt = false;
                    BuildDetails(r);
                }
                RefreshAll();

                long totalBytes = rows.Sum(r => r.Item.SizeBytes);
                int found = rows.Count(r => r.Item.SizeBytes > 0);
                SetHeroValue(DiskCleaner.FormatSize(totalBytes));
                TxtHeroSub.Text = "扫描完成：共 " + found + " 项可清理，合计 " + DiskCleaner.FormatSize(totalBytes)
                                  + " · 勾选后点「开始清理」";
            }
            catch (OperationCanceledException)
            {
                Log("扫描已取消（捕获 OCE）");
                if (gen != _runGen) return;
                _isScanning = false;
                StopScanVisual();
                StopTicker();
                ScanProgress.Visibility = Visibility.Collapsed;
                RefreshAll();
                SetHeroValue(DiskCleaner.FormatSize(Interlocked.Read(ref _discoveredBytes)));
                TxtHeroSub.Text = "已取消扫描 · 已统计部分数据，可重新开始扫描";
            }
            finally
            {
                if (gen == _runGen)
                {
                    _isScanning = false;
                    StopTicker();
                    Log("扫描 finally 收尾");
                    BtnScanClean.Content = "开始扫描";
                    BtnScanClean.Style = (Style)FindResource("AccentButton");
                    BtnScanClean.IsEnabled = true;
                    if (_cts != null) { _cts.Dispose(); _cts = null; }
                    UpdateSummary();
                }
            }
        }

        // ------------------------------------------------------------------
        // 清理
        // ------------------------------------------------------------------

        private async void BtnClean_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleaning || !_hasScanned) return;

            List<ItemUi> selected = AllRows().Where(InSelection).ToList();

            if (selected.Count == 0)
            {
                TxtCleanHint.Text = "勾选的项目没有可清理内容，请重新选择";
                return;
            }

            long bytes = selected.Sum(r => r.Item.SelectedBytes);
            bool hasRegistry = selected.Any(r => r.Item.Kind == CleanupKind.RegistryMru);
            bool hasRisk = selected.Any(r => r.Item.Cautious);
            bool hasRecycle = selected.Any(r => r.Item.Kind == CleanupKind.RecycleBin);
            bool hasDownloads = selected.Any(r => r.Item.Title.IndexOf("下载文件夹", StringComparison.Ordinal) >= 0);

            int detailCount = selected.Sum(r => r.Item.Details.Count(d => d.Checked));
            string body = "将清理 " + selected.Count + " 项" + (detailCount > 0 ? "（" + detailCount + " 条明细）" : "") + "，预计释放 " + DiskCleaner.FormatSize(bytes) + "。\n"
                          + string.Join("、", selected.Take(6).Select(r => r.Item.Title))
                          + (selected.Count > 6 ? " 等" : "")
                          + (hasRecycle ? "\n\n将清空当前账户的回收站，回收站内的文件将无法恢复。" : "")
                          + (hasDownloads ? "\n\n将删除「下载」文件夹内的全部文件（文件夹本身保留）。" : "")
                          + (hasRegistry ? "\n\n注册表痕迹删除前会自动备份到「文档\\清理备份」，可随时还原。" : "")
                          + (hasRisk ? "\n\n其中包含标记为「谨慎清理」的项目，请确认无误。" : "");

            bool confirmed = await ShowConfirmAsync("确认清理", body, false);
            if (!confirmed) return;

            await RunCleanAsync(selected);
        }

        private async Task RunCleanAsync(List<ItemUi> selected)
        {
            int gen = ++_runGen;
            _isCleaning = true;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            _totalWork = selected.Count;
            _doneWork = 0;
            _cleanedBytes = 0;
            _failedCount = 0;
            _currentPath = "准备清理…";

            BtnClean.IsEnabled = false;
            BtnScanClean.IsEnabled = true;
            BtnScanClean.Content = "取消清理";
            BtnScanClean.Style = (Style)FindResource("WarnButton");
            TxtHeroPrefix.Text = "本次释放";
            TxtHeroUnitText.Text = "";
            SetHeroValue("0 MB");
            TxtHeroSub.Text = "正在清理: 准备…";
            ScanProgress.Visibility = Visibility.Visible;
            ScanProgress.Value = 0;
            UpdateSummary();
            StartTicker();
            StartScanVisual(true);

            // 清更新缓存前暂停 Windows 更新 / 传递优化服务，清完（含取消）恢复
            List<string> pausedServices = new List<string>();
            if (selected.Any(r => r.Item.Category == DiskCleaner.CatUpdate))
            {
                TxtHeroSub.Text = "正在暂停更新服务…";
                pausedServices = await Task.Run(() => DiskCleaner.StopUpdateServices());
            }

            try
            {
                await Task.Run(() =>
                {
                    var options = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = _cleanConcurrency,
                        CancellationToken = token
                    };
                    Parallel.ForEach(selected, options, r =>
                    {
                        token.ThrowIfCancellationRequested();
                        _currentPath = "正在清理·" + r.Item.Title;
                        DiskCleaner.CleanResult res = DiskCleaner.CleanItem(r.Item, p => _currentPath = p, token);
                        Interlocked.Add(ref _cleanedBytes, res.Freed);
                        if (res.Failed > 0) Interlocked.Add(ref _failedCount, res.Failed);
                        Interlocked.Increment(ref _doneWork);
                    });
                }, token);
                if (gen != _runGen) { Log("清理结果已作废（已取消）"); return; }

                _isCleaning = false;
                UpdateRing(100);
                StopScanVisual();
                StopTicker();
                ScanProgress.Value = 100;
                ScanProgress.Visibility = Visibility.Collapsed;

                long freed = Interlocked.Read(ref _cleanedBytes);
                _suppressCheck = true;
                foreach (ItemUi r in selected)
                {
                    r.Check.IsChecked = false;
                    r.Item.SizeBytes = 0;
                    r.Item.RecordCount = 0;
                    r.Item.Scanned = false;
                }
                _suppressCheck = false;

                _hasScanned = false;
                _cleanDone = true;
                _suppressCheck = true;
                foreach (ItemUi r in AllRows())
                {
                    r.Item.SizeBytes = 0;
                    r.Item.RecordCount = 0;
                    r.Item.Scanned = false;
                    r.Item.Details.Clear();
                    r.DetailsBuilt = false;
                    if (r.Expanded) BuildDetails(r);
                }
                _suppressCheck = false;
                RefreshAll();

                TxtHeroPrefix.Text = "本次释放";
                TxtHeroUnitText.Text = "";
                SetHeroValue(DiskCleaner.FormatSize(freed));
                CleanScroll.ScrollToTop();
                TxtHeroSub.Text = "清理完成 · 本次释放 " + DiskCleaner.FormatSize(freed) + "，重新扫描可查看剩余垃圾";
                int failed = _failedCount;
                if (failed > 0)
                {
                    TxtHeroSub.Text = "清理完成 · 本次释放 " + DiskCleaner.FormatSize(freed)
                        + " · " + failed + " 个文件被占用或无权限已跳过，关闭相关程序或重启后再扫";
                }
                TxtCleanHint.Text = failed > 0
                    ? "清理完成 · 有 " + failed + " 项未能删除（占用 / 权限），已被跳过"
                    : "清理完成，已清理项已重置，请重新扫描后再继续";
                BtnClean.IsEnabled = false;
            }
            catch (OperationCanceledException)
            {
                if (gen != _runGen) return;
                _isCleaning = false;
                StopTicker();
                ScanProgress.Visibility = Visibility.Collapsed;
                RefreshAll();
                TxtHeroSub.Text = "已取消清理 · 已删除的部分不可恢复";
            }
            finally
            {
                if (pausedServices.Count > 0)
                {
                    List<string> restore = pausedServices;
                    await Task.Run(() => DiskCleaner.RestoreServices(restore));
                }
                if (gen == _runGen)
                {
                    _isCleaning = false;
                    StopTicker();
                    BtnScanClean.Content = "开始扫描";
                    BtnScanClean.Style = (Style)FindResource("AccentButton");
                    BtnScanClean.IsEnabled = true;
                    if (_cts != null) { _cts.Dispose(); _cts = null; }
                    UpdateSummary();
                }
            }
        }

        // ------------------------------------------------------------------
        // 进度刷新（定时读取 volatile 状态，避免跨线程刷 UI）
        // ------------------------------------------------------------------

        private void StartTicker()
        {
            if (_ticker == null)
            {
                _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                _ticker.Tick += (s, e) => TickProgress();
            }
            _ticker.Start();
        }

        private void StopTicker()
        {
            if (_ticker != null) _ticker.Stop();
        }

        private void TickProgress()
        {
            int done = Volatile.Read(ref _doneWork);
            double percent = _totalWork <= 0 ? 0 : Math.Min(100, done * 100.0 / _totalWork);

            if (_isScanning)
            {
                ScanProgress.Value = percent;
                UpdateRing(percent);
                TxtHeroSub.Text = "正在扫描: " + _currentPath;
                SetHeroValue(DiskCleaner.FormatSize(Interlocked.Read(ref _discoveredBytes)));

                // 扫描过程中「已选中」跟着已扫到的项目实时累加
                long selectedNow = 0;
                foreach (ItemUi r2 in AllRows())
                {
                    if (r2.Item.Scanned && IsChecked(r2)) selectedNow += r2.Item.SelectedBytes;
                }
                TxtHeroSelected.Text = DiskCleaner.FormatSize(selectedNow);

                // 每完成一项就把体量补到对应行上，扫完前也能看到各项大小
                bool changed = false;
                foreach (ItemUi r in AllRows())
                {
                    if (!r.Item.Scanned || r.Size.Text.Length > 0) continue;
                    r.Size.Text = r.Item.SizeText;
                    r.Size.Foreground = IsChecked(r) ? Brush("#2B6CB0") : Brush("#6B7280");
                    changed = true;
                }
                if (changed) UpdateGroupStatus();
            }
            else if (_isCleaning)
            {
                ScanProgress.Value = percent;
                UpdateRing(percent);
                TxtHeroSub.Text = "正在清理: " + _currentPath;
                SetHeroValue(DiskCleaner.FormatSize(Interlocked.Read(ref _cleanedBytes)));
            }
        }
    }
}