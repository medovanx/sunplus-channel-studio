using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SunplusChannelStudio.Core;
using SunplusChannelStudio.Core.Import;

namespace SunplusChannelStudio;

/// <summary>Shows what an imported list would change and applies only the ticked changes.</summary>
public partial class ImportWindow : Window
{
    readonly ChannelDatabase _db;
    readonly ImportedList _list;
    List<ChangeRow> _rows = [];

    public int Applied { get; private set; }

    public ImportWindow(ChannelDatabase db, ImportedList list)
    {
        InitializeComponent();
        _db = db;
        _list = list;
        FlowDirection = Loc.Flow;
        // Capture mode for docs screenshots: opens off-screen without taking focus.
        if (Environment.GetEnvironmentVariable("SCS_CAPTURE") == "1")
        {
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -5000;
            Top = 0;
        }
        SourceLabel.Text = Loc.Format("Imp.Source", list.Format, list.Transponders.Count, list.Services.Count);

        foreach (var (sat, i) in db.Satellites.Select((s, i) => (s, i)))
            SatelliteBox.Items.Add(new ComboBoxItem { Content = sat.Name, Tag = i });
        SatelliteBox.SelectedIndex = db.Satellites.Count > 0 ? 0 : -1;

        var positions = list.Positions;
        foreach (var p in positions) PositionBox.Items.Add(new ComboBoxItem { Content = list.Label(p), Tag = p });
        PositionBox.Visibility = PositionLabel.Visibility = positions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (list.PositionNames.Count > 0) PositionLabel.Text = Loc.Get("Imp.SourceSatellite");
        if (positions.Count > 0) PositionBox.SelectedIndex = PreferredPosition(positions);

        BuildColumns();
        Replan();
    }

    /// <summary>Starts on the file position matching the first satellite, when there is one.</summary>
    int PreferredPosition(IReadOnlyList<int> positions)
    {
        if (_db.Satellites.Count == 0) return 0;
        var ours = _db.Satellites[0];
        var all = positions.ToList();
        if (_list.GroupsAreSatelliteIndexes)
        {
            // Merging databases: start on the source satellite with the same name as ours.
            return Math.Max(all.FindIndex(p => _list.PositionNames.GetValueOrDefault(p) == ours.Name), 0);
        }
        int exact = all.FindIndex(p => p == ours.SignedPosition);
        return Math.Max(exact >= 0 ? exact : all.FindIndex(p => Math.Abs(p) == ours.Position), 0);
    }

    void BuildColumns()
    {
        var check = new DataGridCheckBoxColumn
        {
            Binding = new Binding(nameof(ChangeRow.Selected)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = 48,
        };
        ChangesGrid.Columns.Add(check);
        DataGridTextColumn Text(string key, string path, double width, bool data = false) => new()
        {
            Header = Loc.Get(key),
            Binding = new Binding(path),
            IsReadOnly = true,
            Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star),
            ElementStyle = CellStyle(data),
        };
        ChangesGrid.Columns.Add(Text("Imp.Col.Change", nameof(ChangeRow.Kind), 190));
        ChangesGrid.Columns.Add(Text("Imp.Col.Name", nameof(ChangeRow.Name), 0, data: true));
        ChangesGrid.Columns.Add(Text("Imp.Col.Before", nameof(ChangeRow.Before), 170, data: true));
        ChangesGrid.Columns.Add(Text("Imp.Col.After", nameof(ChangeRow.After), 220, data: true));
    }

    /// <summary>
    /// Data cells mix Latin, numbers and Arabic; in the Arabic layout they are laid out left to right
    /// (so "12034 H, SID 4001" keeps its order) but still aligned to the right like the rest.
    /// </summary>
    static Style CellStyle(bool data = false)
    {
        var style = new Style(typeof(TextBlock));
        if (data && Loc.IsRightToLeft)
        {
            style.Setters.Add(new Setter(FlowDirectionProperty, FlowDirection.LeftToRight));
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        }
        style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(8, 0, 12, 0)));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        return style;
    }

    int SatelliteIndex => SatelliteBox.SelectedItem is ComboBoxItem { Tag: int i } ? i : 0;
    int? Position => PositionBox.SelectedItem is ComboBoxItem { Tag: int p } ? p : null;

    void Scrambled_Click(object sender, RoutedEventArgs e) => Replan();

    void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) Replan();
    }

    void Replan()
    {
        if (_db.Satellites.Count == 0) return;
        var changes = ImportPlanner.Plan(_db, SatelliteIndex, _list.At(Position));
        // Scrambled channels are left out unless the user asks for them: a free-to-air receiver cannot show them.
        bool include = ScrambledBox.IsChecked == true;
        foreach (var c in changes.Where(c => c.Scrambled)) c.Selected = include;
        _rows = changes.Where(c => include || !c.Scrambled).Select(c => new ChangeRow(c, UpdateCount)).ToList();
        ChangesGrid.ItemsSource = _rows;
        UpToDate.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
    }

    void UpdateCount()
    {
        int picked = _rows.Count(r => r.Selected);
        CountLabel.Text = Loc.Format("Imp.Selected", picked, _rows.Count);
        ApplyButton.IsEnabled = picked > 0;
    }

    void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(true);
    void SelectNone_Click(object sender, RoutedEventArgs e) => SetAll(false);

    void SetAll(bool value)
    {
        foreach (var r in _rows) r.Selected = value;
        UpdateCount();
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        ChangesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        Applied = ImportPlanner.Apply(_db, SatelliteIndex, _rows.Select(r => r.Change));
        DialogResult = true;
    }

    /// <summary>One change as a grid row.</summary>
    public sealed class ChangeRow(ImportChange change, Action changed) : INotifyPropertyChanged
    {
        public ImportChange Change { get; } = change;
        public event PropertyChangedEventHandler? PropertyChanged;

        public bool Selected
        {
            get => Change.Selected;
            set
            {
                if (Change.Selected == value) return;
                Change.Selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
                changed();
            }
        }

        public string Kind => Loc.Get("Kind." + Change.Kind) + (Change.Scrambled ? Loc.Get("Kind.Scrambled") : "");
        public string Name => Change.Name;
        public string Before => Change.Before;
        public string After => Change.After;
    }
}
