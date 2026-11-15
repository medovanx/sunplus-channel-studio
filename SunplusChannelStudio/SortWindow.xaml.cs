using System.Text.Json.Nodes;
using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using SunplusChannelStudio.Core;

namespace SunplusChannelStudio;

/// <summary>Lets the user put the channel categories in order; the window then groups the list that way.</summary>
public partial class SortWindow : Window
{
    /// <summary>Default order: the categories most viewers look for first.</summary>
    static readonly ChannelCategory[] DefaultOrder =
    [
        ChannelCategory.General, ChannelCategory.News, ChannelCategory.Religious, ChannelCategory.Sports, ChannelCategory.Kids,
        ChannelCategory.Movies, ChannelCategory.Drama, ChannelCategory.Documentary, ChannelCategory.Music,
    ];

    readonly IReadOnlyDictionary<ChannelCategory, int> _counts;

    public IReadOnlyList<ChannelCategory> Order => CategoryList.Items.Cast<ListBoxItem>().Select(i => (ChannelCategory)i.Tag).ToList();
    public bool Alphabetical => AlphabeticalBox.IsChecked == true;
    public bool AlsoRadio => RadioBox.IsChecked == true;

    public SortWindow(IReadOnlyDictionary<ChannelCategory, int> counts, bool hasRadio)
    {
        InitializeComponent();
        FlowDirection = Loc.Flow;
        if (Environment.GetEnvironmentVariable("SCS_CAPTURE") == "1")
        {
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -5000;
            Top = 0;
        }
        _counts = counts;
        RadioBox.Visibility = hasRadio ? Visibility.Visible : Visibility.Collapsed;
        AlphabeticalBox.IsChecked = Settings.GetString("sortAlphabetical") == "true";
        Fill(SavedOrder());
        CategoryList.SelectedIndex = 0;
        CategoryList.PreviewMouseLeftButtonDown += List_PreviewMouseLeftButtonDown;
        CategoryList.PreviewMouseMove += List_PreviewMouseMove;
        CategoryList.DragOver += List_DragOver;
        CategoryList.DragLeave += (_, _) => _line?.Show(null);
        CategoryList.Drop += List_Drop;
    }

    // ---------- Drag to reorder ----------

    Point _dragStart;
    ListBoxItem? _dragItem;
    InsertionAdorner? _line;

    void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = ItemAt(e.OriginalSource as DependencyObject);
    }

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        _line = new InsertionAdorner(CategoryList);
        AdornerLayer.GetAdornerLayer(CategoryList)?.Add(_line);
        try { DragDrop.DoDragDrop(CategoryList, new DataObject(typeof(ListBoxItem), item), DragDropEffects.Move); }
        finally
        {
            AdornerLayer.GetAdornerLayer(CategoryList)?.Remove(_line);
            _line = null;
        }
    }

    /// <summary>The gap the drop lands in (before item N) and the Y of the line to draw.</summary>
    (int Index, double Y) DropGap(DragEventArgs e)
    {
        var over = ItemAt(CategoryList.InputHitTest(e.GetPosition(CategoryList)) as DependencyObject);
        if (over is null)
        {
            var last = CategoryList.Items.Count > 0 ? (ListBoxItem)CategoryList.Items[^1]! : null;
            return (CategoryList.Items.Count, last is null ? 0 : last.TranslatePoint(new Point(0, last.ActualHeight), CategoryList).Y);
        }
        int index = CategoryList.Items.IndexOf(over);
        double top = over.TranslatePoint(new Point(0, 0), CategoryList).Y;
        return e.GetPosition(over).Y > over.ActualHeight / 2 ? (index + 1, top + over.ActualHeight) : (index, top);
    }

    void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ListBoxItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        if (e.Effects == DragDropEffects.Move) _line?.Show(DropGap(e).Y);
    }

    void List_Drop(object sender, DragEventArgs e)
    {
        _line?.Show(null);
        if (e.Data.GetData(typeof(ListBoxItem)) is not ListBoxItem item) return;
        e.Handled = true;
        int from = CategoryList.Items.IndexOf(item);
        int to = DropGap(e).Index;
        if (to > from) to--; // removing the item first shifts later gaps up
        if (to == from) return;
        CategoryList.Items.RemoveAt(from);
        CategoryList.Items.Insert(to, item);
        CategoryList.SelectedItem = item;
    }

    static ListBoxItem? ItemAt(DependencyObject? node)
    {
        while (node is not null and not ListBoxItem)
            node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as ListBoxItem;
    }

    /// <summary>Opens the user's own rules file, creating it from the shipped rules the first time.</summary>
    void EditRules_Click(object sender, RoutedEventArgs e)
    {
        var path = MainWindow.RulesPath;
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, CategoryRules.DefaultJson());
            }
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { Process.Start("notepad.exe", $"\"{path}\""); } // no app registered for .json
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, Loc.Get("Sort.EditRules"), MessageBoxButton.OK, MessageBoxImage.Warning,
                MessageBoxResult.None, Loc.BoxOptions);
        }
    }

    /// <summary>The order the user chose last time, with any category it lacks added at the end.</summary>
    static List<ChannelCategory> SavedOrder()
    {
        var saved = (Settings.GetList("categoryOrder") ?? [])
            .Select(s => Enum.TryParse<ChannelCategory>(s, out var c) ? c : (ChannelCategory?)null)
            .OfType<ChannelCategory>().Distinct().ToList();
        saved.AddRange(DefaultOrder.Where(c => !saved.Contains(c)));
        return saved;
    }

    void Fill(IEnumerable<ChannelCategory> order)
    {
        CategoryList.Items.Clear();
        foreach (var c in order)
        {
            var row = new DockPanel();
            var count = new TextBlock
            {
                Text = Loc.Format("Sort.Count", _counts.GetValueOrDefault(c)),
                Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush"),
            };
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            row.Children.Add(new TextBlock { Text = Loc.Get("Cat." + c) });
            CategoryList.Items.Add(new ListBoxItem { Content = row, Tag = c, Padding = new Thickness(10, 8, 10, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
    }

    void Up_Click(object sender, RoutedEventArgs e) => Shift(-1);
    void Down_Click(object sender, RoutedEventArgs e) => Shift(1);

    void Shift(int step)
    {
        int i = CategoryList.SelectedIndex;
        int j = i + step;
        if (i < 0 || j < 0 || j >= CategoryList.Items.Count) return;
        var item = CategoryList.Items[i];
        CategoryList.Items.RemoveAt(i);
        CategoryList.Items.Insert(j, item);
        CategoryList.SelectedIndex = j;
        CategoryList.ScrollIntoView(item);
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        Fill(DefaultOrder);
        CategoryList.SelectedIndex = 0;
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        Settings.Set("categoryOrder", new JsonArray(Order.Select(c => (JsonNode)JsonValue.Create(c.ToString())).ToArray()));
        Settings.Set("sortAlphabetical", Alphabetical ? "true" : "false");
        DialogResult = true;
    }
}
