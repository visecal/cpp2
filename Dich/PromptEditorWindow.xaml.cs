using System.Windows;
using System.Windows.Input;

namespace subphimv1
{
    public partial class PromptEditorWindow : Window
    {
        // Các thuộc tính public để TranslateWindow có thể lấy dữ liệu sau khi dialog đóng
        public string UserDefinedPromptText { get; private set; }
        public bool UseSystemBasePromptSetting { get; private set; }

        public PromptEditorWindow(string currentUserPrompt, bool useSystemPrompt)
        {
            InitializeComponent();

            // Gán giá trị hiện tại vào các control trên UI
            txtUserPrompt.Text = currentUserPrompt;
            chkUseSystemBasePrompt.IsChecked = useSystemPrompt;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Lấy giá trị từ các control và gán vào các thuộc tính public
            UserDefinedPromptText = txtUserPrompt.Text;
            UseSystemBasePromptSetting = chkUseSystemBasePrompt.IsChecked ?? true;

            // Đặt DialogResult thành true để báo hiệu đã lưu thành công
            this.DialogResult = true;
            this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        // Window Controls
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) this.DragMove();
        }
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Nút X trên title bar tương đương với việc hủy
            this.DialogResult = false;
            this.Close();
        }
    }
}