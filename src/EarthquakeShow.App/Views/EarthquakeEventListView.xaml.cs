using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is EarthquakeEventListViewModel viewModel)
        {
            await viewModel.RefreshAsync();
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
}
