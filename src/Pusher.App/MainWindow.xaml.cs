using System.Windows;

namespace Pusher.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Max/restore icon sync happens declaratively via a DataTrigger on WindowState.
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
