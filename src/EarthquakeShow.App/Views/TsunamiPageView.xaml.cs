using System.Windows;
using System.Windows.Controls;

namespace EarthquakeShow.App.Views;

public partial class TsunamiPageView : UserControl
{
    public TsunamiPageView()
    {
        InitializeComponent();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.TsunamiPageViewModel viewModel)
        {
            await viewModel.RefreshAsync();
        }
    }
}
