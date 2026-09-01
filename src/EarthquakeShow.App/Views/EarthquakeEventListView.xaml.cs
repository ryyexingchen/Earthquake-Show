using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EarthquakeShow.App.ViewModels;

namespace EarthquakeShow.App.Views;

public partial class EarthquakeEventListView : UserControl
{
    public event EventHandler? RequestOpenDetails;

    public EarthquakeEventListView()
    {
        InitializeComponent();
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public void ScrollToSelectedItem()
    {
        if (EventListBox.SelectedItem is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => EventListBox.ScrollIntoView(EventListBox.SelectedItem)));
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is EarthquakeEventListViewModel viewModel)
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

    private void OnClearFiltersClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is EarthquakeEventListViewModel viewModel)
        {
            viewModel.ClearFilters();
        }
    }

    private void OnEventListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            DataContext is EarthquakeEventListViewModel { SelectedItem: not null })
        {
            RequestOpenDetails?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void OnEventListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0)
        {
            ScrollToSelectedItem();
        }
    }
}
