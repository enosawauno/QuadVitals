using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using QuadVitals.Core;
using QuadVitals.UI;
using AppSettings = QuadVitals.Core.Settings;


namespace QuadVitals.Settings
{
    /// <summary>关于：版本、设置/日志位置、许可提示。</summary>
    internal sealed partial class AboutPage : UserControl
    {
        /// <summary>
        /// Public repository address, shown on the "源码地址" row. Kept as one constant here so
        /// the About page and the README cannot drift apart.
        /// </summary>
        private const string RepoUrl = "https://github.com/enosawauno/QuadVitals";

        /// <summary>
        /// The licence's own page. The "源码许可证" row carries an external-link arrow, so it has
        /// to open a browser page; the full local text stays one click away on the "授权说明"
        /// button in the version card.
        /// </summary>
        private const string LicenceUrl = "https://polyformproject.org/licenses/noncommercial/1.0.0/";

        private const string AuthorName = "ENOSAWA";

        /// <summary>
        /// 同作者的另一个项目发布仓库，开发者卡最后一行指向它。
        /// </summary>
        private const string FileOopsUrl = "https://github.com/enosawauno/FileOops-Release";

        /// <summary>版本明细：更新卡末尾那一行指向仓库里的 CHANGELOG。</summary>
        private const string ChangelogUrl = "https://github.com/enosawauno/QuadVitals/blob/main/CHANGELOG.md";

        private readonly SettingsWindow _window;

        public AboutPage(SettingsWindow window)
        {
            _window = window;
            StackPanel root = new StackPanel
            {
                Margin = new Thickness(Theme.PagePadding, 0, Theme.PagePadding, Theme.PagePadding)
            };
            root.Children.Add(new PageHeading("关于", "版本、源码地址与第三方许可"));

            // 更新和校验卡自带进度与状态，位置固定在页头下方。
            root.Children.Add(BuildUpdateCard());
            root.Children.Add(new Border { Height = Theme.CardGap });

            Card info = new Card("日志和文件");
            info.Content.Children.Add(new KeyValueRow("共享内存", MetricsChannel.MapName));
            info.Content.Children.Add(new KeyValueRow("任务栏条", "原生 C++ DeskBand（GDI）"));
            info.Content.Children.Add(new KeyValueRow("温度来源", "LibreHardwareMonitor（MPL 2.0）"));
            info.Content.Children.Add(new KeyValueRow("授权", "PolyForm Noncommercial 1.0.0（源码公开，禁止商业使用）"));

            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            FlatButton openFolder = new FlatButton("打开日志目录");
            openFolder.Click += (sender, e) => OpenFolder();
            buttons.Children.Add(openFolder);

            // Licence and third-party notices ship next to the binaries (MPL 2.0 / Apache 2.0
            // require it), so the About page just opens them from the install directory.
            FlatButton openNotices = new FlatButton("第三方许可");
            AutomationProperties.SetAutomationId(openNotices, "AboutLicenses");
            openNotices.Margin = new Thickness(8, 0, 0, 0);
            openNotices.Click += (sender, e) => OpenDocument("THIRD-PARTY-NOTICES.txt");
            buttons.Children.Add(openNotices);

            FlatButton openTerms = new FlatButton("许可全文");
            AutomationProperties.SetAutomationId(openTerms, "AboutLicense");
            openTerms.Margin = new Thickness(8, 0, 0, 0);
            // Local copy: the English text that ships in the install directory (MPL/Apache
            // notices live in the same folder, behind the "第三方许可" button next to it).
            openTerms.Click += (sender, e) => OpenDocument("LICENSE");
            buttons.Children.Add(openTerms);
            info.Content.Children.Add(buttons);

            root.Children.Add(info);
            root.Children.Add(new Border { Height = Theme.CardGap });
            root.Children.Add(BuildDeveloperCard());

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = root
            };
        }


        private static void OpenFolder()
        {
            string directory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuadVitals");
            if (!System.IO.Directory.Exists(directory)) System.IO.Directory.CreateDirectory(directory);
            Process.Start("explorer.exe", "\"" + directory + "\"");
        }

        /// <summary>
        /// 开发者卡片：作者 / 源码地址 / 源码许可证 / 另一个项目，右侧绿色链接 + 外链箭头。
        /// 行内容只读，不写死 URL 以外的信息，避免关于页和真实仓库说法不一致。
        /// </summary>
        private FrameworkElement BuildDeveloperCard()
        {
            Card developer = new Card("开发者");

            developer.Content.Children.Add(LinkRow("作者", AuthorName, null));
            developer.Content.Children.Add(LinkRow("源码地址", "GitHub", OpenRepository));
            developer.Content.Children.Add(LinkRow("源码许可证", "PolyForm Noncommercial 1.0.0", OpenLicencePage));
            developer.Content.Children.Add(LinkRow("FileOops", "GitHub", OpenFileOops));
            return developer;
        }

        /// <summary>
        /// One label/value row: grey label on the left, green clickable value with the
        /// MDL2 "open in new window" arrow on the right. A null action makes it plain text.
        /// </summary>
        private static FrameworkElement LinkRow(string label, string value, Action onClick)
        {
            Grid row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock caption = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = Theme.TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(caption, 0);
            row.Children.Add(caption);

            StackPanel right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            bool clickable = onClick != null;
            TextBlock text = new TextBlock
            {
                Text = value,
                FontSize = 12,
                Foreground = clickable ? Theme.AccentGreenBrush : Theme.TextPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            right.Children.Add(text);

            if (!clickable)
            {
                Grid.SetColumn(right, 1);
                row.Children.Add(right);
                return row;
            }

            TextBlock arrow = new TextBlock
            {
                Text = "\uE8A7",   // Segoe MDL2: open in new window
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Foreground = Theme.AccentGreenBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0)
            };
            right.Children.Add(arrow);

            // The link is the value block on the right, not the whole row: the label stays inert.
            // A container whose Background is null is not hit-testable in WPF, so without the
            // transparent brush only the glyphs reacted and the gap before the arrow (or beside a
            // short value) did nothing. The padding makes the block easy to hit without widening
            // it into the row.
            Border target = new Border
            {
                Child = right,
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                Padding = new Thickness(6, 3, 0, 3)
            };
            Action action = onClick;
            target.MouseLeftButtonUp += (sender, e) => action();
            target.MouseEnter += (sender, e) =>
            {
                text.Foreground = Theme.AccentGreenSoftBrush;
                arrow.Foreground = Theme.AccentGreenSoftBrush;
            };
            target.MouseLeave += (sender, e) =>
            {
                text.Foreground = Theme.AccentGreenBrush;
                arrow.Foreground = Theme.AccentGreenBrush;
            };

            Grid.SetColumn(target, 1);
            row.Children.Add(target);
            return row;
        }

        private void OpenChangelog()
        {
            try
            {
                Process.Start(ChangelogUrl);
            }
            catch (Exception ex)
            {
                _window.ShowNotice("无法打开更新明细：" + ex.Message, true);
            }
        }

        private void OpenLicencePage()
        {
            try
            {
                Process.Start(LicenceUrl);
            }
            catch (Exception ex)
            {
                _window.ShowNotice("无法打开许可页面：" + ex.Message, true);
            }
        }

        private void OpenRepository()
        {
            if (string.IsNullOrEmpty(RepoUrl))
            {
                _window.ShowNotice("源码地址尚未配置（AboutPage.RepoUrl）", true);
                return;
            }
            try
            {
                Process.Start(RepoUrl);
            }
            catch (Exception ex)
            {
                _window.ShowNotice("无法打开源码地址：" + ex.Message, true);
            }
        }

        private void OpenFileOops()
        {
            try
            {
                Process.Start(FileOopsUrl);
            }
            catch (Exception ex)
            {
                _window.ShowNotice("无法打开 FileOops 地址：" + ex.Message, true);
            }
        }

        private static string DocumentTitle(string fileName)
        {
            switch (fileName)
            {
                case "LICENSE": return "许可全文 · PolyForm Noncommercial 1.0.0";
                case "LICENSE.zh.md": return "许可要点（中文）";
                case "THIRD-PARTY-NOTICES.txt": return "第三方许可与署名";
                default: return fileName;
            }
        }

        /// <summary>
        /// Shows a licence file that ships next to the executables inside the app (dark overlay),
        /// instead of handing it to Notepad. Looks next to the exe first, then the agent output
        /// directory, then the repository root (dev layout is build\out\ui).
        /// </summary>
        private void OpenDocument(string fileName)
        {
            try
            {
                string baseDir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
                string[] candidates =
                {
                    System.IO.Path.Combine(baseDir, fileName),
                    System.IO.Path.Combine(baseDir, "..", "agent", fileName),
                    System.IO.Path.Combine(baseDir, "..", "..", "..", fileName)
                };
                foreach (string candidate in candidates)
                {
                    string full = System.IO.Path.GetFullPath(candidate);
                    if (!System.IO.File.Exists(full)) continue;

                    string text = System.IO.File.ReadAllText(full);
                    _window.ShowOverlay(new DocumentViewerDialog(_window).Build(DocumentTitle(fileName), text));
                    return;
                }
                _window.ShowNotice("找不到 " + fileName + "（安装目录里应有一份）", true);
            }
            catch (Exception ex)
            {
                _window.ShowNotice("无法读取 " + fileName + "：" + ex.Message, true);
            }
        }
    }
}
