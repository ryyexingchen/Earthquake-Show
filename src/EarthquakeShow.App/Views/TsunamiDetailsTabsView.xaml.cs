using System.Windows;

namespace EarthquakeShow.App.Views;

public partial class TsunamiDetailsTabsView : System.Windows.Controls.UserControl
{
    public TsunamiDetailsTabsView()
    {
        InitializeComponent();
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
