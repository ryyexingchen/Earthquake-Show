using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace EarthquakeShow.App.Views;

public partial class TsunamiDetailsTabsView : System.Windows.Controls.UserControl
{
    private bool _timelineSelectionRequested;
    private bool _forecastAreaSelectionRequested;

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
        if (!_timelineSelectionRequested)
        {
            return;
        }

        _timelineSelectionRequested = false;
        if (TsunamiTimelineListBox.SelectedItem is not ViewModels.TsunamiTimelineItemDisplay item ||
            DataContext is not ViewModels.TsunamiPageViewModel viewModel)
        {
            return;
        }

        viewModel.SelectReport(item.EventId, item.SourceId, item.SourceMessageId);
    }

    private void OnTimelinePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ArmTimelineSelectionRequest();
    }

    private void OnTimelinePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or
            Key.PageUp or Key.PageDown or Key.Enter or Key.Space)
        {
            ArmTimelineSelectionRequest();
        }
    }

    private void ArmTimelineSelectionRequest()
    {
        _timelineSelectionRequested = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => _timelineSelectionRequested = false));
    }

    private void OnForecastAreaSelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_forecastAreaSelectionRequested ||
            ForecastAreaListBox.SelectedValue is not string areaCode ||
            DataContext is not ViewModels.TsunamiPageViewModel viewModel)
        {
            return;
        }

        _forecastAreaSelectionRequested = false;
        viewModel.SelectForecastArea(areaCode);
    }

    private void OnForecastAreaPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ArmForecastAreaSelectionRequest();
    }

    private void OnForecastAreaPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or
            Key.PageUp or Key.PageDown or Key.Enter or Key.Space)
        {
            ArmForecastAreaSelectionRequest();
        }
    }

    private void ArmForecastAreaSelectionRequest()
    {
        _forecastAreaSelectionRequested = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() => _forecastAreaSelectionRequested = false));
    }
}
