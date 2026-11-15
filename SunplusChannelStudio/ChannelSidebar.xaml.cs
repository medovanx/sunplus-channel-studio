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
/// The Live TV channel list beside the picture: every public stream in the iptv-org catalogue, with search and
/// country and category filters. Picking a row plays it in the same window.
/// </summary>
public partial class ChannelSidebar : UserControl
{
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    // Arrowing through the list waits for a short pause, so passing rows does not open a stream for each one.
    readonly DispatcherTimer _chooseDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };
    StreamCatalog? _catalog;
    List<LiveRow> _all = [];
    bool _ready;

    /// <summary>A channel was picked: its name and its streams, best first.</summary>
    public event Action<string, IReadOnlyList<LiveStream>>? ChannelChosen;

    public ChannelSidebar()
    {
        InitializeComponent();
        BuildColumns();
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); ApplyFilter(); };
        _chooseDelay.Tick += (_, _) => { _chooseDelay.Stop(); Choose(); };
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
                    new Setter(TextBlock.MarginProperty, new Thickness(8, 4, 10, 4)),
                    new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis),
                },
            },
        };
        ChannelGrid.Columns.Add(Text("Col.Name", nameof(LiveRow.Name), star: true));
        ChannelGrid.Columns.Add(Text("Live.Country", nameof(LiveRow.CountryCode)));
        ChannelGrid.Columns.Add(Text("Col.Quality", nameof(LiveRow.Quality)));
    }

    /// <param name="load">Loads the catalogue (the main window's shared, cached copy), reporting each download.</param>
    public async Task LoadAsync(Func<IProgress<string>, Task<StreamCatalog>> load)
    {
        try
        {
            _catalog = await load(new Progress<string>(file => PlaceholderText.Text = Loc.Format("Watch.Downloading", file)));
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
    }

    /// <summary>Puts the cursor in the search box.</summary>
    public void FocusSearch() => SearchBox.Focus();

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
        _suppress = true; // a new list is not a pick
        ChannelGrid.ItemsSource = shown;
        _suppress = false;
        CountLabel.Text = Loc.Format("Live.Count", shown.Count, _all.Count);
        Placeholder.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderText.Text = Loc.Get("Live.NoMatch");
    }

    bool _suppress;

    void ChannelGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || ChannelGrid.SelectedItem is null) return;
        // A click plays at once; keyboard moves wait for a pause.
        _chooseDelay.Stop();
        if (Mouse.LeftButton == MouseButtonState.Pressed) Choose();
        else _chooseDelay.Start();
    }

    void ChannelGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _chooseDelay.Stop();
        Choose();
    }

    /// <summary>Moves to the next or previous channel in the list and plays it, like a remote's channel keys.</summary>
    public void Step(int by)
    {
        if (ChannelGrid.Items.Count == 0) return;
        int next = Math.Clamp(ChannelGrid.SelectedIndex + by, 0, ChannelGrid.Items.Count - 1);
        if (next == ChannelGrid.SelectedIndex) return;
        _suppress = true;
        ChannelGrid.SelectedIndex = next;
        _suppress = false;
        ChannelGrid.ScrollIntoView(ChannelGrid.SelectedItem);
        _chooseDelay.Stop();
        _chooseDelay.Start();
    }

    LiveRow? _playing;

    void Choose()
    {
        if (ChannelGrid.SelectedItem is not LiveRow row || _catalog is null || row == _playing) return;
        _playing = row;
        ChannelChosen?.Invoke(row.Name, _catalog.StreamsOf(row.Channel));
    }

    /// <summary>One catalogue channel as shown in the list; everything is worked out once when the list loads.</summary>
    sealed class LiveRow(CatalogChannel channel, StreamCatalog catalog)
    {
        public CatalogChannel Channel { get; } = channel;
        public string Name { get; } = channel.Name;
        public string Country { get; } = CountryName(channel.Country);
        public string CountryCode { get; } = channel.Country ?? "";
        public ChannelCategory CategoryValue { get; } = CategoryRules.Default.FromCatalog(channel.Categories) ?? ChannelCategory.General;
        public string Quality { get; } = catalog.StreamsOf(channel).FirstOrDefault()?.Quality ?? "";

        static string CountryName(string? code)
        {
            if (code is null) return "";
            try { return new RegionInfo(code).EnglishName; }
            catch (ArgumentException) { return code; } // codes .NET does not know, such as UK or international ones
        }
    }
}
