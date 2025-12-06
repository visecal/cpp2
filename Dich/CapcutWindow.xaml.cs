using subphimv1.Services;
using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace subphimv1
{
    public partial class CapcutWindow : Window
    {
        public CapcutWindow()
        {
            InitializeComponent();
        }
        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                this.DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            // *** FIX: Use standard WindowState instead of custom method ***
            this.WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close(); // The Closing event in App.xaml.cs will handle the rest
        }


        private void Log(string message)
        {
            // Đảm bảo cập nhật UI trên đúng luồng
            Dispatcher.Invoke(() =>
            {
                LogTextBlock.Text += $"\n[{DateTime.Now:HH:mm:ss}] {message}";
                LogScrollViewer.ScrollToEnd();
            });
        }

        private async void ActivateButton_Click(object sender, RoutedEventArgs e)
        {
            ActivateButton.IsEnabled = false;
            DeactivateButton.IsEnabled = false;

            LogTextBlock.Text = "Bắt đầu quá trình...";

            var progress = new Progress<string>(Log);
            bool success = await CapcutPatcher.ActivateAsync(progress);

            if (success)
            {
                DeactivateButton.IsEnabled = true;
            }
            else
            {
                ActivateButton.IsEnabled = true;
                DeactivateButton.IsEnabled = true;
            }
        }

        private async void DeactivateButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close(); // Sự kiện Closing sẽ xử lý việc dọn dẹp
        }

        private async void Window_Closing(object sender, CancelEventArgs e)
        {
            if (CapcutPatcher.IsActive)
            {
                e.Cancel = true;
                ActivateButton.IsEnabled = false;
                DeactivateButton.IsEnabled = false;

                var progress = new Progress<string>(Log);
                await CapcutPatcher.CleanupAsync(progress);

                Dispatcher.Invoke(() => this.Closing -= Window_Closing);
                this.Close();
            }
        }
    }
}