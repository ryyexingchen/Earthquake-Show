using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace EarthquakeShow.App.Views;

public partial class TsunamiPageView : UserControl
{
    private bool _reportSelectionRequested;

    public TsunamiPageView()
    {
        InitializeComponent();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.TsunamiPageViewModel viewModel)
        {
            try
            {
                await viewModel.RefreshAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void OnReportSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_reportSelectionRequested)
        {
            return;
        }

        _reportSelectionRequested = false;
        if (TsunamiReportListBox.SelectedItem is not ViewModels.TsunamiEventReportDisplay display ||
            DataContext is not ViewModels.TsunamiPageViewModel viewModel)
        {
            return;
        }

        viewModel.SelectedReport = display.Report;
    }

    private void OnReportPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ArmReportSelectionRequest();
    }

    private void OnReportPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or
            Key.PageUp or Key.PageDown or Key.Enter or Key.Space)
        {
            ArmReportSelectionRequest();
        }
    }

    private void ArmReportSelectionRequest()
    {
        _reportSelectionRequested = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => _reportSelectionRequested = false));
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
