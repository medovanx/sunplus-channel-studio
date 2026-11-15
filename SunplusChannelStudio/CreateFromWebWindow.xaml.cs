using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using SunplusChannelStudio.Core;
using SunplusChannelStudio.Core.Import;

namespace SunplusChannelStudio;

/// <summary>
/// Starts a new database without one from the receiver: pick a satellite position from the open
/// dtv-scan-tables lists and a receiver format, and the satellite and its transponders are built.
/// </summary>
public partial class CreateFromWebWindow : Window
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(1) };

    /// <summary>The new database, set when the window closes with Create.</summary>
    public ChannelDatabase? Database { get; private set; }

    public CreateFromWebWindow()
    {
        InitializeComponent();
        FlowDirection = Loc.Flow;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SunplusChannelStudio"); // GitHub's API requires one
        foreach (var type in Enum.GetValues<DatabaseType>())
            FormatBox.Items.Add(new ComboBoxItem { Content = type.Label(), Tag = type });
        FormatBox.SelectedIndex = 0;
        if (Environment.GetEnvironmentVariable("SCS_CAPTURE") == "1")
        {
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -5000;
            Top = 0;
        }
        Loaded += async (_, _) => await LoadPositions();
        Closed += (_, _) => _http.Dispose();
    }

    int? Position => PositionBox.SelectedItem is ComboBoxItem { Tag: int p } ? p : null;

    List<ComboBoxItem> _items = [];

    /// <summary>
    /// Narrows the list to positions whose label or satellites contain every typed word, ignoring case,
    /// spaces and dots, so "7w", "7.0 W", "nile" and "19.2" all find their position.
    /// </summary>
    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        static string Fold(string s) => new string(s.Where(c => !char.IsWhiteSpace(c) && c != '.').ToArray()).ToLowerInvariant();
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Fold).ToList();
        var keep = PositionBox.SelectedItem;
        PositionBox.Items.Clear();
        foreach (var item in _items.Where(i => words.All(w => Fold((string)i.Content).Contains(w)
                     || Fold(ImportedList.PositionLabel((int)i.Tag).Replace(".0", "")).Contains(w))))
            PositionBox.Items.Add(item);
        // A typed position such as "7w" picks that position, not 17W or 117W, which also contain it.
        var exact = words.Count == 1
            ? PositionBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Fold(ImportedList.PositionLabel((int)i.Tag).Replace(".0", "")) == words[0]
                                                                        || Fold(ImportedList.PositionLabel((int)i.Tag)) == words[0])
            : null;
        if (exact is not null) PositionBox.SelectedItem = exact;
        else if (keep is not null && PositionBox.Items.Contains(keep)) PositionBox.SelectedItem = keep;
        else if (PositionBox.Items.Count == 1) PositionBox.SelectedIndex = 0;
        if (words.Count > 0 && PositionBox.Items.Count > 1) PositionBox.IsDropDownOpen = SearchBox.IsKeyboardFocusWithin;
        LoadLabel.Text = PositionBox.Items.Count == 0 ? Loc.Get("New.NoMatch") : "";
        LoadLabel.Visibility = PositionBox.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task LoadPositions()
    {
        try
        {
            var positions = await ScanTableSource.ListPositionsAsync(_http);
            int saved = int.TryParse(Settings.GetString("newDatabasePosition"), out var s) ? s : int.MinValue;
            _items = positions.Select(p => new ComboBoxItem
            {
                Content = $"{ImportedList.PositionLabel(p.Position)}   {string.Join(", ", p.Satellites)}",
                Tag = p.Position,
            }).ToList();
            foreach (var item in _items) PositionBox.Items.Add(item);
            PositionBox.SelectedIndex = positions.ToList().FindIndex(p => p.Position == saved);
            PositionBox.IsEnabled = SearchBox.IsEnabled = true;
            if (ShowActivated) SearchBox.Focus(); // not in capture runs, which must not take focus
            LoadLabel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            LoadLabel.Text = Loc.Format("Imp.WebFailed", ex.Message);
        }
    }

    void PositionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => CreateButton.IsEnabled = Position is not null;

    async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (Position is not { } position || FormatBox.SelectedItem is not ComboBoxItem { Tag: DatabaseType type }) return;
        CreateButton.IsEnabled = PositionBox.IsEnabled = SearchBox.IsEnabled = false;
        LoadLabel.Text = Loc.Get("Imp.Downloading");
        LoadLabel.Visibility = Visibility.Visible;
        try
        {
            var list = await ScanTableSource.FetchAsync(_http, [position]);
            Database = DatabaseBuilder.Create(list, [position], type);
            Settings.Set("newDatabasePosition", position.ToString());
            DialogResult = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or ImportFormatException)
        {
            LoadLabel.Text = ex is ImportFormatException ? Loc.Get("Imp.WebNone") : Loc.Format("Imp.WebFailed", ex.Message);
            CreateButton.IsEnabled = PositionBox.IsEnabled = SearchBox.IsEnabled = true;
        }
    }
}
