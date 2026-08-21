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

    private void OnObservationTreeSelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is EarthquakeObservationTreeNode node)
        {
            ViewModel?.SelectObservationNode(node);
        }
    }

    private void OnExpandObservationTreeClick(object sender, RoutedEventArgs e)
    {
        SetObservationTreeExpanded(true);
    }

    private void OnCollapseObservationTreeClick(object sender, RoutedEventArgs e)
    {
        SetObservationTreeExpanded(false);
    }

    private void SetObservationTreeExpanded(bool isExpanded)
    {
        foreach (object item in ObservationTreeView.Items)
        {
            if (ObservationTreeView.ItemContainerGenerator.ContainerFromItem(item)
                is TreeViewItem container)
            {
                SetTreeItemExpanded(container, isExpanded);
            }
        }
    }

    private static void SetTreeItemExpanded(TreeViewItem item, bool isExpanded)
    {
        item.IsExpanded = isExpanded;
        item.UpdateLayout();
        foreach (object child in item.Items)
        {
            if (item.ItemContainerGenerator.ContainerFromItem(child) is TreeViewItem childContainer)
            {
                SetTreeItemExpanded(childContainer, isExpanded);
            }
        }
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
