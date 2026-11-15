using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SunplusChannelStudio.Core;

namespace SunplusChannelStudio;

/// <summary>Lists channels that are scrambled, placeholders, silent or duplicated, and removes the ticked ones.</summary>
public partial class CleanupWindow : Window
{
    readonly ChannelDatabase _db;
    readonly List<ItemRow> _all;
    List<ItemRow> _shown = [];

    public int Removed { get; private set; }

    public CleanupWindow(ChannelDatabase db)
    {
        InitializeComponent();
        _db = db;
        FlowDirection = Loc.Flow;
        if (Environment.GetEnvironmentVariable("SCS_CAPTURE") == "1")
        {
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -5000;
            Top = 0;
        }
        _all = Cleanup.Find(db).Select(i => new ItemRow(i, db, UpdateCount)).ToList();
        BuildColumns();
        Refilter();
    }

    void BuildColumns()
    {
        ItemsGrid.Columns.Add(new DataGridCheckBoxColumn
        {
            Binding = new Binding(nameof(ItemRow.Selected)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = 48,
        });
        DataGridTextColumn Text(string key, string path, double width, bool data = false)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(8, 0, 12, 0)));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            if (data && Loc.IsRightToLeft)
            {
                // Latin names and numbers keep their order inside the Arabic layout.
                style.Setters.Add(new Setter(FlowDirectionProperty, FlowDirection.LeftToRight));
                style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            }
            return new DataGridTextColumn
            {
                Header = Loc.Get(key),
                Binding = new Binding(path),
                IsReadOnly = true,
                Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star),
                ElementStyle = style,
            };
        }
        ItemsGrid.Columns.Add(Text("Cln.Col.List", nameof(ItemRow.List), 90));
        ItemsGrid.Columns.Add(Text("Col.Number", nameof(ItemRow.Number), 70, data: true));
        ItemsGrid.Columns.Add(Text("Col.Name", nameof(ItemRow.Name), 0, data: true));
        ItemsGrid.Columns.Add(Text("Col.Transponder", nameof(ItemRow.Transponder), 140, data: true));
        ItemsGrid.Columns.Add(Text("Cln.Col.Reason", nameof(ItemRow.Reason), 260));
    }

    CleanupReason Wanted =>
        (ScrambledBox.IsChecked == true ? CleanupReason.Scrambled : 0)
        | (PlaceholderBox.IsChecked == true ? CleanupReason.Placeholder : 0)
        | (NoStreamsBox.IsChecked == true ? CleanupReason.NoStreams : 0)
        | (DuplicateBox.IsChecked == true ? CleanupReason.Duplicate : 0);

    void Category_Click(object sender, RoutedEventArgs e) => Refilter();

    void Refilter()
    {
        var wanted = Wanted;
        _shown = _all.Where(r => (r.Item.Reasons & wanted) != 0).ToList();
        ItemsGrid.ItemsSource = _shown;
        Nothing.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
    }

    void UpdateCount()
    {
        int picked = _shown.Count(r => r.Selected);
        CountLabel.Text = Loc.Format("Imp.Selected", picked, _shown.Count);
        RemoveButton.IsEnabled = picked > 0;
    }

    void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(true);
    void SelectNone_Click(object sender, RoutedEventArgs e) => SetAll(false);

    void SetAll(bool value)
    {
        foreach (var r in _shown) r.Selected = value;
        UpdateCount();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var picked = _shown.Where(r => r.Selected).Select(r => r.Item.Channel).ToList();
        if (MessageBox.Show(this, Loc.Format("Cln.Confirm", picked.Count), Loc.Get("Cln.Title"),
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel, Loc.BoxOptions) != MessageBoxResult.OK)
            return;
        Removed = Cleanup.Remove(_db, picked);
        DialogResult = true;
    }

    public sealed class ItemRow(CleanupItem item, ChannelDatabase db, Action changed) : INotifyPropertyChanged
    {
        bool _selected = true;
        public CleanupItem Item { get; } = item;
        public event PropertyChangedEventHandler? PropertyChanged;

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
                changed();
            }
        }

        public string List => Loc.Get(Item.List == ChannelKind.Radio ? "Cln.Radio" : "Cln.Tv");
        public int Number => Item.Number;
        public string Name => Item.Channel.Name;

        public string Transponder => db.TransponderOf(Item.Channel) is { } tp
            ? $"{tp.Frequency} {(tp.Polarization == 0 ? "V" : "H")} {tp.SymbolRate}"
            : "";

        public string Reason => string.Join(Loc.Get("List.Separator"),
            Enum.GetValues<CleanupReason>().Where(r => r != CleanupReason.None && Item.Reasons.HasFlag(r))
                .Select(r => Loc.Get("Reason." + r)));
    }
}
