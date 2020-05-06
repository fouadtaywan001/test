using System.Windows;
using Pusher.App.ViewModels;

namespace Pusher.App.Views;

public partial class EditScriptWindow : Window
{
    public EditScriptWindow(EditScriptViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Saved += () => { DialogResult = true; Close(); };
        vm.Cancelled += () => { DialogResult = false; Close(); };
    }

    private void OnTitleCloseClick(object sender, RoutedEventArgs e) => Close();
}
