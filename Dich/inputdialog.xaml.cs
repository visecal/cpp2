using System.Windows;
using System.Windows.Input;

namespace subphimv1
{
    public partial class InputDialog : Window
    {
        public string Answer { get; private set; } = "";

        public InputDialog(string prompt, string defaultText = "")
        {
            InitializeComponent();
            this.Title = prompt; // Đặt tiêu đề cửa sổ
            lblPrompt.Text = prompt; // Đặt nội dung câu hỏi
            txtName.Text = defaultText;

            // Focus và chọn toàn bộ text để người dùng có thể gõ đè ngay lập tức
            this.Loaded += (s, e) =>
            {
                txtName.Focus();
                txtName.SelectAll();
            };
        }

        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtName.Text))
            {
                CustomMessageBox.Show("Nội dung không được để trống.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Answer = txtName.Text.Trim();
            DialogResult = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            this.Close();
        }

        // Window Controls
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) this.DragMove();
        }
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            this.Close();
        }
    }
}