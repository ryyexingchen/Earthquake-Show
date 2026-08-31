using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

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

    private void OnReportSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (TsunamiReportListBox.SelectedItem is not EarthquakeShow.Core.Models.JmaTsunamiReport report ||
            DataContext is not ViewModels.TsunamiPageViewModel viewModel)
        {
            return;
        }

        viewModel.SelectedReport = report;
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

    private void OnObservationStationSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (ObservationStationListBox.SelectedItem is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => ObservationStationListBox.ScrollIntoView(
                ObservationStationListBox.SelectedItem)));
    }
}
