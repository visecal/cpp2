using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace subphimv1
{
    // Lớp này đại diện cho một tin nhắn trong cuộc hội thoại
    public class ChatMessage : INotifyPropertyChanged
    {
        private bool _isExpanded = true;

        public string MessageContent { get; set; }
        public bool IsUserInput { get; set; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                _isExpanded = value;
                OnPropertyChanged(); // Thông báo cho UI biết rằng thuộc tính này đã thay đổi
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}