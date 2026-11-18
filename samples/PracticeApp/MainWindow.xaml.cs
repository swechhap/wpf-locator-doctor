using System.Windows;

namespace PracticeApp;

public sealed record Order(string OrderNumber, string Customer);

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var orders = new List<Order>
        {
            new("SO-1001", "Contoso"),
            new("SO-1002", "Fabrikam"),
            new("SO-1003", "Northwind"),
            new("SO-1004", "Adventure Works"),
        };
        OrdersList.ItemsSource = orders;
        OrdersGrid.ItemsSource = orders;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) =>
        new SettingsWindow { Owner = this }.Show();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
