using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using SunplusChannelStudio.Core;
using SunplusChannelStudio.Core.Streams;

namespace SunplusChannelStudio;

/// <summary>
/// Browses every public stream in the iptv-org catalogue, with no channel database needed:
/// search, filter by country and category, and watch in the player.
/// </summary>
public partial class LiveTvWindow : Window
{
    readonly Func<IProgress<string>, Task<StreamCatalog>> _load;
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    StreamCatalog? _catalog;
    List<LiveRow> _all = [];
    bool _ready;

    /// <param name="load">Loads the catalogue (the main window's shared, cached copy), reporting each download.</param>
    public LiveTvWindow(Func<IProgress<string>, Task<StreamCatalog>> load)
    {
        InitializeComponent();
        FlowDirection = Loc.Flow;
        _load = load;
        BuildColumns();
        if (Environment.GetEnvironmentVariable("SCS_CAPTURE") == "1")
        {
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -5000;
            Top = 0;
        }
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); ApplyFilter(); };
        Loaded += async (_, _) => await LoadCatalog();
    }

    void BuildColumns()
    {
        DataGridTextColumn Text(string key, string path, bool star = false) => new()
        {
            Header = Loc.Get(key),
            Binding = new Binding(path),
            Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto,
            ElementStyle = new Style(typeof(TextBlock))
            {
                Setters =
                {
                    new Setter(TextBlock.MarginProperty, new Thickness(8, 4, 12, 4)),
                    new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis),
                },
            },
        };
        ChannelGrid.Columns.Add(Text("Col.Name", nameof(LiveRow.Name), star: true));
        ChannelGrid.Columns.Add(Text("Live.Country", nameof(LiveRow.Country)));
        ChannelGrid.Columns.Add(Text("Col.Category", nameof(LiveRow.Category)));
        ChannelGrid.Columns.Add(Text("Col.Quality", nameof(LiveRow.Quality)));
        ChannelGrid.Columns.Add(Text("Live.Sources", nameof(LiveRow.Sources)));
    }

    async Task LoadCatalog()
    {
        try
        {
            _catalog = await _load(new Progress<string>(file => PlaceholderText.Text = Loc.Format("Watch.Downloading", file)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.IO.IOException or System.Text.Json.JsonException)
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            PlaceholderText.Text = Loc.Format("Watch.Failed", ex.Message);
            return;
        }

        // Building 9,000 rows (country names, categories, best quality) is done off the UI thread.
        var catalog = _catalog;
        _all = await Task.Run(() => catalog.Channels.Select(c => new LiveRow(c, catalog)).ToList());

        CountryBox.Items.Add(new ComboBoxItem { Content = Loc.Get("Live.AllCountries"), Tag = "" });
        foreach (var (code, name) in _all.Where(r => r.Channel.Country is not null)
                     .Select(r => (Code: r.Channel.Country!, r.Country)).Distinct().OrderBy(c => c.Country, StringComparer.CurrentCultureIgnoreCase))
            CountryBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        CategoryBox.Items.Add(new ComboBoxItem { Content = Loc.Get("Live.AllCategories"), Tag = null });
        foreach (var c in Enum.GetValues<ChannelCategory>())
            CategoryBox.Items.Add(new ComboBoxItem { Content = Loc.Get("Cat." + c), Tag = c });

        var savedCountry = Settings.GetString("liveCountry") ?? "";
        CountryBox.SelectedItem = CountryBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == savedCountry) ?? CountryBox.Items[0];
        var savedCategory = Settings.GetString("liveCategory");
        CategoryBox.SelectedItem = CategoryBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == savedCategory) ?? CategoryBox.Items[0];

        SearchBox.IsEnabled = CountryBox.IsEnabled = CategoryBox.IsEnabled = true;
        LoadingBar.Visibility = Visibility.Collapsed;
        _ready = true;
        ApplyFilter();
        SearchBox.Focus();
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Waits for a pause in typing, so the list is not rebuilt on every key.
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Settings.Set("liveCountry", (CountryBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "");
        Settings.Set("liveCategory", (CategoryBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "");
        ApplyFilter();
    }

    void ApplyFilter()
    {
        if (!_ready) return;
        var country = (CountryBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var category = (CategoryBox.SelectedItem as ComboBoxItem)?.Tag as ChannelCategory?;
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = _all.Where(r =>
                (country.Length == 0 || r.Channel.Country == country) &&
                (category is null || r.CategoryValue == category) &&
                words.All(w => r.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();
        ChannelGrid.ItemsSource = shown;
        CountLabel.Text = Loc.Format("Live.Count", shown.Count, _all.Count);
        Placeholder.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderText.Text = Loc.Get("Live.NoMatch");
        WatchButton.IsEnabled = ChannelGrid.SelectedItem is not null;
    }

    void ChannelGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        WatchButton.IsEnabled = ChannelGrid.SelectedItem is not null;

    void ChannelGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on a row plays; one on the header or scroll bar does nothing.
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(ChannelGrid, d) is DataGridRow) Watch();
    }

    void ChannelGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Watch();
    }

    void Watch_Click(object sender, RoutedEventArgs e) => Watch();

    void Watch()
    {
        if (ChannelGrid.SelectedItem is not LiveRow row || _catalog is null) return;
        new PlayerWindow(row.Name, _catalog.StreamsOf(row.Channel)) { Owner = this }.Show();
    }

    /// <summary>One catalogue channel as shown in the list; everything is worked out once when the list loads.</summary>
    sealed class LiveRow(CatalogChannel channel, StreamCatalog catalog)
    {
        public CatalogChannel Channel { get; } = channel;
        public string Name { get; } = channel.Name;
        public string Country { get; } = CountryName(channel.Country);
        public ChannelCategory CategoryValue { get; } = CategoryRules.Default.FromCatalog(channel.Categories) ?? ChannelCategory.General;
        public string Category => Loc.Get("Cat." + CategoryValue);
        public string Quality { get; } = catalog.StreamsOf(channel).FirstOrDefault()?.Quality ?? "";
        public int Sources { get; } = catalog.StreamsOf(channel).Count;

        static string CountryName(string? code)
        {
            if (code is null) return "";
            try { return new RegionInfo(code).EnglishName; }
            catch (ArgumentException) { return code; } // codes .NET does not know, such as UK or international ones
        }
    }
}
