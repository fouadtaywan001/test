using System.Windows;
using Pusher.App.ViewModels;

namespace Pusher.App.Views;

public partial class RescheduleWindow : Window
{
    public RescheduleWindow(RescheduleViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseWithSuccess = () => { DialogResult = true; Close(); };
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
