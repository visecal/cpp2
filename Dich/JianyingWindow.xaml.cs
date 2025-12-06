using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using subphimv1.Services;

namespace subphimv1
{
    public partial class JianyingWindow : Window
    {
        public JianyingWindow()
        {
            InitializeComponent();
            // Đăng ký lắng nghe sự kiện khi cửa sổ được tạo
            JianyingPatcher.CleanupCompleted += OnPatcherCleanupCompleted;
            // Cập nhật trạng thái nút lần đầu khi cửa sổ được tải
            this.Loaded += (s, e) => UpdateButtonsState();
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

        /// <summary>
        /// Hàm trung tâm để cập nhật trạng thái của các nút dựa trên trạng thái của Patcher.
        /// </summary>
        private void UpdateButtonsState()
        {
            // Nút "Gỡ bỏ & Đóng" LUÔN LUÔN được bật.
            DeactivateButton.IsEnabled = true;

            // Nút "Tải & Kích hoạt" chỉ được bật khi bản vá KHÔNG hoạt động.
            ActivateButton.IsEnabled = !JianyingPatcher.IsActive;
        }

        private void Log(string message) => Dispatcher.Invoke(() =>
        {
            LogTextBlock.Text += $"\n[{DateTime.Now:HH:mm:ss}] {message}";
            LogScrollViewer.ScrollToEnd();
        });

        private void UpdateProgress(double percentage) => Dispatcher.Invoke(() =>
        {
            DownloadProgressBar.Value = percentage;
            DownloadProgressBar.Visibility = (percentage > 0 && percentage < 100) ? Visibility.Visible : Visibility.Collapsed;
        });

        private async void ActivateButton_Click(object sender, RoutedEventArgs e)
        {
            // Tạm thời vô hiệu hóa cả hai nút để tránh thao tác thừa
            ActivateButton.IsEnabled = false;
            DeactivateButton.IsEnabled = false;
            LogTextBlock.Text = "Bắt đầu quá trình...";

            var progress = new Progress<string>(Log);
            var downloadProgress = new Progress<double>(UpdateProgress);

            await JianyingPatcher.ActivateAsync(progress, downloadProgress);

            // Sau khi quá trình kết thúc, cập nhật lại trạng thái các nút
            UpdateButtonsState();
        }

        /// <summary>
        /// Xử lý khi JianyingPro tự động đóng và Patcher tự động dọn dẹp.
        /// </summary>
        private void OnPatcherCleanupCompleted(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                Log("FuckCut đã đóng.");
                UpdateButtonsState();
            });
        }

        /// <summary>
        /// Xử lý khi người dùng chủ động bấm nút "Gỡ bỏ & Đóng".
        /// </summary>
        private void DeactivateButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close(); // Chức năng duy nhất là đóng cửa sổ.
        }

        /// <summary>
        /// Xử lý logic dọn dẹp an toàn khi cửa sổ bị đóng.
        /// </summary>
        private async void Window_Closing(object sender, CancelEventArgs e)
        {
            // Hủy đăng ký sự kiện để tránh rò rỉ bộ nhớ
            JianyingPatcher.CleanupCompleted -= OnPatcherCleanupCompleted;

            if (JianyingPatcher.IsActive)
            {
                e.Cancel = true;

                // Chỉ vô hiệu hóa nút Kích hoạt trong lúc dọn dẹp.
                // Nút Đóng vẫn phải bấm được.
                ActivateButton.IsEnabled = false;
                await JianyingPatcher.CleanupAsync(new Progress<string>(Log));

                // Sau khi dọn dẹp xong, thực sự đóng cửa sổ
                Dispatcher.Invoke(() => this.Closing -= Window_Closing);
                this.Close();
            }
        }
    }
}