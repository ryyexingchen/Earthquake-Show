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

    private void OnCopyRawXmlClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.TsunamiPageViewModel viewModel ||
            !viewModel.CanCopyRawXml)
        {
            return;
        }

        Clipboard.SetText(viewModel.RawXmlText);
        viewModel.MarkRawXmlCopied();
    }
}
