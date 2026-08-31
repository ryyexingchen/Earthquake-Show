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

    private void OnPreviousReportClick(object sender, RoutedEventArgs e)
    {
        (DataContext as ViewModels.TsunamiPageViewModel)?.GoPreviousReport();
    }

    private void OnNextReportClick(object sender, RoutedEventArgs e)
    {
        (DataContext as ViewModels.TsunamiPageViewModel)?.GoNextReport();
    }

    private void OnReturnToLatestClick(object sender, RoutedEventArgs e)
    {
        (DataContext as ViewModels.TsunamiPageViewModel)?.ReturnToLatestReport();
    }

    private void OnTimelineSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TsunamiTimelineListBox.SelectedItem is not ViewModels.TsunamiTimelineItemDisplay item ||
            DataContext is not ViewModels.TsunamiPageViewModel viewModel)
        {
            return;
        }

        viewModel.SelectReport(item.EventId, item.SourceId, item.SourceMessageId);
    }
}
