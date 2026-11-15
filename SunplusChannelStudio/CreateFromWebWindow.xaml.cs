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

    async Task LoadPositions()
    {
        try
        {
            var positions = await ScanTableSource.ListPositionsAsync(_http);
            int saved = int.TryParse(Settings.GetString("newDatabasePosition"), out var s) ? s : int.MinValue;
            foreach (var (position, satellites) in positions)
                PositionBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{ImportedList.PositionLabel(position)}   {string.Join(", ", satellites)}",
                    Tag = position,
                });
            PositionBox.SelectedIndex = positions.ToList().FindIndex(p => p.Position == saved);
            PositionBox.IsEnabled = true;
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
        CreateButton.IsEnabled = PositionBox.IsEnabled = false;
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
            CreateButton.IsEnabled = PositionBox.IsEnabled = true;
        }
    }
}
