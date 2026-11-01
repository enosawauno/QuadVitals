using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuadVitals.UI;

namespace QuadVitals.Setup
{
    /// <summary>
    /// The only page before the work starts: the options, one line about the admin prompt, one line about
    /// the copies that will be closed, and the two buttons. Nothing here asks a second time - a separate
    /// "is it OK to close them?" page repeated the decision the user already made by pressing 安装.
    /// </summary>
    internal sealed partial class SetupWindow
    {
        // ---- welcome -------------------------------------------------------------------

        private UIElement BuildWelcome()
        {
            StackPanel stack = new StackPanel();

            stack.Children.Add(Heading(_uninstall ? "卸载 QuadVitals " + _version : "安装 QuadVitals " + _version));
            stack.Children.Add(Subtitle(_uninstall
                ? "将从这台电脑移除 QuadVitals 的文件、任务栏组件和登录启动项"
                : "任务栏常驻显示 CPU / 内存 / 温度 / 网速"));

            if (_uninstall)
            {
                stack.Children.Add(Group(BuildUninstallOptions()));
            }
            else
            {
                stack.Children.Add(Group(BuildInstallOptions()));
            }

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            FlatButton cancel = new FlatButton("取消");
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += (sender, e) => CancelWork();
            buttons.Children.Add(cancel);

            FlatButton primary = new FlatButton(_uninstall ? "卸载" : "安装", true);
            primary.MinWidth = 96;
            primary.Click += (sender, e) => StartWork();
            buttons.Children.Add(primary);

            return PageLayout(stack, buttons);
        }

        private UIElement BuildInstallOptions()
        {
            StackPanel stack = new StackPanel();

            // No "安装位置" label: the field is the only input on this page, so it starts on the
            // same left edge as the check boxes and ends on the same right edge. The path is long
            // enough that spending 78 px on a label only shortened the value.
            // Rounded corners come from DarkTextBox - the product's own field - instead of a stock
            // TextBox, which cannot be rounded and looked square next to the rest of the card.
            Grid row = new Grid();

            _installDirBox = new DarkTextBox
            {
                Text = InstallerCore.DefaultInstallDir,
                IsReadOnly = true,
                Focusable = false,
                // Right padding reserves the strip the inline button sits on, so a long path never
                // runs underneath it.
                Padding = new Thickness(10, 2, 74, 2)
            };
            row.Children.Add(_installDirBox);

            FlatButton browse = new FlatButton("更改…");
            browse.Height = 22;
            browse.Padding = new Thickness(8, 0, 8, 0);
            browse.FontSize = 12;
            browse.Background = Brushes.Transparent;
            browse.Foreground = Theme.AccentGreenBrush;
            browse.Focusable = false;
            browse.Cursor = Cursors.Hand;
            browse.HorizontalAlignment = HorizontalAlignment.Right;
            browse.VerticalAlignment = VerticalAlignment.Center;
            browse.Margin = new Thickness(0, 0, 6, 0);
            browse.Click += (sender, e) => BrowseInstallDir();
            row.Children.Add(browse);

            stack.Children.Add(row);

            _startAgent = new DarkCheckBox
            {
                Content = "安装完成后立即启动 QuadVitals",
                Foreground = Theme.TextPrimaryBrush,
                FontSize = 13,
                IsChecked = true,
                Margin = new Thickness(0, 18, 0, 0)
            };
            stack.Children.Add(_startAgent);

            _desktopShortcut = new DarkCheckBox
            {
                Content = "在桌面创建快捷方式",
                Foreground = Theme.TextPrimaryBrush,
                FontSize = 13,
                IsChecked = true,
                Margin = new Thickness(0, 12, 0, 0)
            };
            stack.Children.Add(_desktopShortcut);

            return stack;
        }


        private UIElement BuildUninstallOptions()
        {
            StackPanel stack = new StackPanel();

            // Same shape as the install page: the path sits in a full-width read-only field instead of a
            // label/value row, so both cards line up on the same edges.
            TextBox path = new DarkTextBox
            {
                Text = InstallerCore.DefaultInstallDir,
                IsReadOnly = true,
                Focusable = false
            };
            stack.Children.Add(path);

            _keepSettings = new DarkCheckBox
            {
                Content = "保留我的设置",
                Foreground = Theme.TextPrimaryBrush,
                FontSize = 13,
                IsChecked = true,
                Margin = new Thickness(0, 18, 0, 0)
            };
            stack.Children.Add(_keepSettings);

            return stack;
        }

        private void BrowseInstallDir()
        {
            using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择 QuadVitals 的安装位置";
                dialog.SelectedPath = _installDirBox.Text;
                dialog.ShowNewFolderButton = true;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _installDirBox.Text = System.IO.Path.Combine(dialog.SelectedPath, "QuadVitals");
                }
            }
        }
    }
}
