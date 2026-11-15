using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
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
