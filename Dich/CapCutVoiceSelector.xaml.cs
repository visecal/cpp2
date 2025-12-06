using subphimv1.Services;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace subphimv1.Dich
{
    public partial class CapCutVoiceSelector : Window
    {
        public CapCutVoice SelectedVoice { get; private set; }

        private List<CapCutVoice> _allVoices;
        private string _selectedCategory = null;
        private string _selectedGender = null;
        private string _selectedLanguage = null;

        public CapCutVoiceSelector()
        {
            InitializeComponent();
            InitializeData();
        }

        private void InitializeData()
        {
            _allVoices = CapCutTtsService.AllVoices;

            // Populate Category ComboBox
            var categories = new List<string> { "Tất cả" };
            categories.AddRange(CapCutTtsService.GetCategories());
            CategoryComboBox.ItemsSource = categories;
            CategoryComboBox.SelectedIndex = 0;

            // Populate Gender ComboBox
            var genders = new List<string> { "Tất cả", "Nam", "Nữ", "Khác" };
            GenderComboBox.ItemsSource = genders;
            GenderComboBox.SelectedIndex = 0;

            // Populate Language ComboBox
            var languages = new List<string> { "Tất cả" };
            languages.AddRange(CapCutTtsService.GetLanguages());
            LanguageComboBox.ItemsSource = languages;
            LanguageComboBox.SelectedIndex = 0;

            // Load all voices initially
            RefreshVoiceList();
        }

        private void RefreshVoiceList()
        {
            var filtered = CapCutTtsService.FilterVoices(
                category: _selectedCategory,
                gender: _selectedGender,
                language: _selectedLanguage
            );

            VoiceListView.ItemsSource = filtered;
            VoiceCountText.Text = $"Tổng số: {filtered.Count} giọng nói";

            // Select first item if available
            if (filtered.Count > 0)
            {
                VoiceListView.SelectedIndex = 0;
            }
        }

        private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CategoryComboBox.SelectedItem is string category)
            {
                _selectedCategory = category == "Tất cả" ? null : category;
            }

            if (GenderComboBox.SelectedItem is string gender)
            {
                _selectedGender = gender == "Tất cả" ? null : gender;
            }

            if (LanguageComboBox.SelectedItem is string language)
            {
                _selectedLanguage = language == "Tất cả" ? null : language;
            }

            RefreshVoiceList();
        }

        private void ClearFilters_Click(object sender, RoutedEventArgs e)
        {
            CategoryComboBox.SelectedIndex = 0;
            GenderComboBox.SelectedIndex = 0;
            LanguageComboBox.SelectedIndex = 0;
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (VoiceListView.SelectedItem is CapCutVoice voice)
            {
                SelectedVoice = voice;
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show("Vui lòng chọn một giọng nói.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void VoiceListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (VoiceListView.SelectedItem is CapCutVoice voice)
            {
                SelectedVoice = voice;
                DialogResult = true;
                Close();
            }
        }
    }
}
