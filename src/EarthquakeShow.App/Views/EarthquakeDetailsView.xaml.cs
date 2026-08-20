using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using EarthquakeShow.App.ViewModels;

namespace EarthquakeShow.App.Views;

public partial class EarthquakeDetailsView : UserControl
{
    public EarthquakeDetailsView()
    {
        InitializeComponent();
    }

    private EarthquakeDetailsViewModel? ViewModel => DataContext as EarthquakeDetailsViewModel;

    public void FocusDetails()
    {
        DetailsTabs.Focus();
    }

    private void OnPreviousReportClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.GoPreviousReport();
    }

    private void OnNextReportClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.GoNextReport();
    }

    private void OnReturnToLatestClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ReturnToLatestReport();
    }

    private void OnFocusHypocenterClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.FocusHypocenter();
    }

    private void OnShowRawDataClick(object sender, RoutedEventArgs e)
    {
        DetailsTabs.SelectedIndex = 3;
        RawPayloadTextBox.Focus();
    }

    private void OnCopyEventIdClick(object sender, RoutedEventArgs e)
    {
        string? eventId = ViewModel?.SummaryFields
            .FirstOrDefault(field => field.Label == "事件 ID")
            ?.Value;
        CopyToClipboard(eventId);
    }

    private void OnCopyRawDataClick(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(ViewModel?.RawPayload);
    }

    private void OnRawWrapChanged(object sender, RoutedEventArgs e)
    {
        bool isWrapped = WrapRawTextToggle.IsChecked == true;
        RawPayloadTextBox.TextWrapping = isWrapped ? TextWrapping.Wrap : TextWrapping.NoWrap;
        RawPayloadTextBox.HorizontalScrollBarVisibility = isWrapped
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
    }

    private static void CopyToClipboard(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        try
        {
            Clipboard.SetText(value);
        }
        catch (COMException)
        {
            MessageBox.Show(
                "剪贴板暂时不可用，请稍后重试。",
                "Earthquake Show",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }
}
