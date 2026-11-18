using System.Windows;

namespace PracticeApp;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
