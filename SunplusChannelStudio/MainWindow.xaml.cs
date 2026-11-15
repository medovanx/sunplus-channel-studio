using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SunplusChannelStudio.Core;

namespace SunplusChannelStudio;

public partial class MainWindow : Window
{

    ChannelDatabase? _db;
    string? _path;
    bool _dirty;
    bool _loading;
    readonly ObservableCollection<ChannelRow> _tv = [];
    readonly ObservableCollection<ChannelRow> _radio = [];
    readonly HashSet<string> _backedUp = new(StringComparer.OrdinalIgnoreCase);
    Point _dragStart;
    bool _dragArmed;
    DataGridRow? _pendingSingleSelect;
    InsertionAdorner? _insertion;
    readonly Dictionary<DataGridColumn, string> _headerKeys = [];

    public MainWindow()
    {
        InitializeComponent();
        // Capture mode for docs screenshots: opens off-screen without taking focus.
        if (Environment.GetEnvironmentVariable("SCS_CAPTURE") == "1")
        {
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -5000;
            Top = 0;
        }
        foreach (var type in Enum.GetValues<DatabaseType>())
            FormatBox.Items.Add(new ComboBoxItem { Content = type.Label(), Tag = type });

        foreach (var grid in new[] { TvGrid, RadioGrid })
        {
            BuildChannelColumns(grid);
            grid.PreviewKeyDown += ChannelGrid_PreviewKeyDown;
            grid.PreviewMouseLeftButtonDown += ChannelGrid_PreviewMouseLeftButtonDown;
            grid.PreviewMouseLeftButtonUp += ChannelGrid_PreviewMouseLeftButtonUp;
            grid.PreviewMouseMove += ChannelGrid_PreviewMouseMove;
            grid.Drop += ChannelGrid_Drop;
            grid.DragOver += ChannelGrid_DragOver;
            grid.DragLeave += ChannelGrid_DragLeave;
            grid.SelectionChanged += (_, _) => UpdateStatus();
        }
        TvGrid.ItemsSource = _tv;
        RadioGrid.ItemsSource = _radio;
        foreach (var col in TpGrid.Columns.Concat(SatGrid.Columns).OfType<DataGridTextColumn>())
            col.ElementStyle = CellStyle(right: false);

        foreach (var col in new[] { TvGrid, RadioGrid, TpGrid, SatGrid }.SelectMany(g => g.Columns))
            _headerKeys[col] = (string)col.Header;
        Loc.Changed += ApplyLanguage;
        Closed += (_, _) => Loc.Changed -= ApplyLanguage;
        ApplyLanguage();

        Row.Edited += MarkDirty;
        Loaded += (_, _) =>
        {
            var arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
            if (arg is not null && File.Exists(arg)) OpenFile(arg);
        };
        UpdateChrome();
    }

    // ---------- Columns ----------

    static void BuildChannelColumns(DataGrid grid)
    {
        DataGridTextColumn Text(string header, string path, double width, bool readOnly = true, bool right = false, bool latin = false)
        {
            var binding = new Binding(path);
            if (!readOnly)
            {
                binding.ValidatesOnExceptions = true;
                binding.UpdateSourceTrigger = UpdateSourceTrigger.LostFocus;
            }
            var col = new DataGridTextColumn
            {
                Header = header,
                Binding = binding,
                IsReadOnly = readOnly,
                Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star),
            };
            col.ElementStyle = CellStyle(right, latin);
            return col;
        }

        grid.Columns.Add(Text("Col.Number", nameof(ChannelRow.Number), 70, right: true));
        grid.Columns.Add(Text("Col.Name", nameof(ChannelRow.Name), 0, readOnly: false));
        grid.Columns.Add(Text("Col.Type", nameof(ChannelRow.Definition), 60));
        grid.Columns.Add(Text("Col.Favorite", nameof(ChannelRow.Favorite), 100));
        grid.Columns.Add(Text("Col.Transponder", nameof(ChannelRow.Frequency), 140, latin: true));
        grid.Columns.Add(Text("Col.Satellite", nameof(ChannelRow.Satellite), 130));
        grid.Columns.Add(Text("Col.Sid", nameof(ChannelRow.ServiceId), 70, right: true));
        grid.Columns.Add(Text("Col.Video", nameof(ChannelRow.VideoPid), 84, right: true));
        grid.Columns.Add(Text("Col.AudioA", nameof(ChannelRow.AudioPidA), 76, right: true));
        grid.Columns.Add(Text("Col.AudioB", nameof(ChannelRow.AudioPidB), 76, right: true));
        grid.Columns.Add(Text("Col.Pcr", nameof(ChannelRow.PcrPid), 70, right: true));
        grid.Columns.Add(Text("Col.Pmt", nameof(ChannelRow.PmtPid), 70, right: true));
    }

    /// <summary>Cell text style; <paramref name="latin"/> keeps Latin-and-number text in order inside the Arabic layout.</summary>
    static Style CellStyle(bool right, bool latin = false)
    {
        var style = new Style(typeof(TextBlock));
        if (latin) style.Setters.Add(new Setter(FlowDirectionProperty, FlowDirection.LeftToRight));
        style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(8, 0, 12, 0)));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        if (right) style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        return style;
    }

    // ---------- Open / save ----------

    void Open_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dialog = new OpenFileDialog { Filter = Loc.Get("Dlg.FileFilter"), Title = Loc.Get("Dlg.OpenTitle") };
        if (dialog.ShowDialog(this) == true) OpenFile(dialog.FileName, confirmed: true);
    }

    void OpenFile(string path, bool confirmed = false)
    {
        if (!confirmed && !ConfirmDiscard()) return;
        ChannelDatabase db;
        try { db = ChannelDatabase.Load(path); }
        catch (Exception ex) when (ex is DatabaseFormatException or IOException or UnauthorizedAccessException)
        {
            Say(ex is DatabaseFormatException f ? Loc.Get("Fmt." + f.Problem) : ex.Message, "Dlg.CannotOpen", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _loading = true;
        _db = db;
        _path = path;
        Fill(_tv, db.TvChannels);
        Fill(_radio, db.RadioChannels);
        FillTransponders();
        FormatBox.SelectedItem = FormatBox.Items.Cast<ComboBoxItem>().First(i => (DatabaseType)i.Tag == db.Type);
        SearchBox.Text = string.Empty;
        _loading = false;
        _dirty = false;
        Tabs.SelectedItem = TvTab;
        UpdateChrome();
    }

    void Fill(ObservableCollection<ChannelRow> rows, List<Channel> channels)
    {
        rows.Clear();
        foreach (var c in channels) rows.Add(new ChannelRow(c, _db!));
        Renumber(rows);
    }

    void FillTransponders()
    {
        var db = _db!;
        var all = db.TvChannels.Concat(db.RadioChannels).ToList();
        TpGrid.ItemsSource = db.Transponders
            .Select((tp, i) => new TransponderRow(tp, i, db) { Channels = all.Count(c => c.TransponderIndex == i) })
            .ToList();
        SatGrid.ItemsSource = db.Satellites
            .Select((s, i) => new SatelliteRow(s, i) { Transponders = db.Transponders.Count(t => t.SatelliteIndex == i) })
            .ToList();
    }

    void Save_Executed(object sender, ExecutedRoutedEventArgs e) => SaveTo(_path!);

    void SaveAs_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = Loc.Get("Dlg.FileFilter"),
            Title = Loc.Get("Dlg.SaveAsTitle"),
            FileName = Path.GetFileName(_path),
            InitialDirectory = Path.GetDirectoryName(_path),
        };
        if (dialog.ShowDialog(this) == true && dialog.FileName is { Length: > 0 } file) SaveTo(file);
    }

    bool SaveTo(string path)
    {
        if (_db is null) return false;
        CommitEdits();
        _db.TvChannels.Clear();
        _db.TvChannels.AddRange(_tv.Select(r => r.Channel));
        _db.RadioChannels.Clear();
        _db.RadioChannels.AddRange(_radio.Select(r => r.Channel));
        _db.Type = SelectedFormat;
        try
        {
            // Keep one copy of the file as it was before this session first overwrote it.
            if (File.Exists(path) && _backedUp.Add(path))
                File.Copy(path, path + ".bak", overwrite: true);
            _db.Save(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say(ex.Message, "Dlg.CannotSave", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _path = path;
        _dirty = false;
        UpdateChrome();
        StatusLabel.Text = Loc.Format("Status.Saved", Path.GetFileName(path), _db.Type.Label());
        return true;
    }

    DatabaseType SelectedFormat => FormatBox.SelectedItem is ComboBoxItem { Tag: DatabaseType t } ? t : DatabaseType.NormalDkOtp;

    void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _db is null || SelectedFormat == _db.Type) return;
        MarkDirty();
        StatusLabel.Text = Loc.Format("Status.FormatOnSave", SelectedFormat.Label());
    }

    void HasFile_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _db is not null;

    bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var answer = Say(Loc.Get("Dlg.SaveFirst"), "Dlg.Unsaved", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => SaveTo(_path!),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        CommitEdits();
        if (!ConfirmDiscard()) e.Cancel = true;
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) OpenFile(files[0]);
    }

    // ---------- Channel actions ----------

    DataGrid? ActiveGrid => Tabs.SelectedItem == TvTab ? TvGrid : Tabs.SelectedItem == RadioTab ? RadioGrid : null;
    ObservableCollection<ChannelRow>? ActiveRows => ActiveGrid == TvGrid ? _tv : ActiveGrid == RadioGrid ? _radio : null;

    List<ChannelRow> Selected() =>
        ActiveGrid?.SelectedItems.Cast<ChannelRow>().ToList() ?? [];

    bool Filtering => !string.IsNullOrWhiteSpace(SearchBox.Text);

    bool CanReorder()
    {
        if (!Filtering) return true;
        StatusLabel.Text = Loc.Get("Status.ClearSearch");
        return false;
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => MoveBy(-1);
    void MoveDown_Click(object sender, RoutedEventArgs e) => MoveBy(1);

    void MoveBy(int step)
    {
        var rows = ActiveRows;
        var picked = Selected();
        if (rows is null || picked.Count == 0 || !CanReorder()) return;
        CommitEdits();
        var order = picked.Select(rows.IndexOf).OrderBy(i => i).ToList();
        if (step < 0 && order[0] == 0) return;
        if (step > 0 && order[^1] == rows.Count - 1) return;
        foreach (var i in step < 0 ? order : Enumerable.Reverse(order)) rows.Move(i, i + step);
        AfterReorder(rows, picked);
    }

    void MoveTo_Click(object sender, RoutedEventArgs e)
    {
        var rows = ActiveRows;
        var picked = Selected();
        if (rows is null || picked.Count == 0 || !CanReorder()) return;
        CommitEdits();
        var target = NumberDialog.Ask(this, Loc.Get("Dlg.MoveTitle"),
            Loc.Format("Dlg.MoveAsk", Loc.Channels(picked), rows.Count),
            picked.Min(r => r.Number), 1, rows.Count);
        if (target is null) return;
        MoveRows(rows, picked, target.Value - 1);
    }

    /// <summary>Moves rows as one block, in their current order, so the first lands at <paramref name="index"/>.</summary>
    void MoveRows(ObservableCollection<ChannelRow> rows, List<ChannelRow> picked, int index) =>
        AfterReorder(rows, Reorder.MoveBlock(rows, picked, index));

    void AfterReorder(ObservableCollection<ChannelRow> rows, List<ChannelRow> keepSelected)
    {
        Renumber(rows);
        var grid = rows == _tv ? TvGrid : RadioGrid;
        grid.SelectedItems.Clear();
        foreach (var r in keepSelected) grid.SelectedItems.Add(r);
        grid.ScrollIntoView(keepSelected[0]);
        MarkDirty();
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var rows = ActiveRows;
        var picked = Selected();
        if (rows is null || picked.Count == 0) return;
        CommitEdits();
        var what = Loc.Channels(picked);
        if (Say(Loc.Format("Dlg.DeleteAsk", what), "Dlg.DeleteTitle", MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK) return;
        int first = picked.Min(rows.IndexOf);
        foreach (var r in picked) rows.Remove(r);
        Renumber(rows);
        RefreshTransponderCounts();
        MarkDirty();
        if (rows.Count > 0) ActiveGrid!.SelectedItem = rows[Math.Min(first, rows.Count - 1)];
        StatusLabel.Text = Loc.Format("Status.Deleted", what);
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        var grid = ActiveGrid;
        if (grid?.SelectedItem is null) return;
        grid.CurrentCell = new DataGridCellInfo(grid.SelectedItem, grid.Columns[1]);
        grid.BeginEdit();
    }

    void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected().Count == 0) return;
        var menu = new ContextMenu { FlowDirection = Loc.Flow, PlacementTarget = FavoriteButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        AddFavoriteItems(menu.Items);
        menu.IsOpen = true;
    }

    void ChannelMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.FlowDirection = Loc.Flow;
        var favorites = menu.Items.OfType<MenuItem>().First(i => (string?)i.Tag == "Favorites");
        favorites.Items.Clear();
        AddFavoriteItems(favorites.Items);
    }

    void AddFavoriteItems(ItemCollection items)
    {
        var picked = Selected();
        foreach (var group in FavoriteGroup.All)
        {
            var item = new MenuItem
            {
                Header = group.Label,
                IsCheckable = true,
                IsChecked = picked.Count > 0 && picked.All(r => r.Channel.Favorite == group.Value),
            };
            item.Click += (_, _) =>
            {
                foreach (var r in Selected()) r.SetFavorite(group.Value);
                StatusLabel.Text = Loc.Format("Status.FavoriteSet", group.Label);
            };
            items.Add(item);
        }
    }

    void ChannelGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (IsEditing(grid)) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Delete: Delete_Click(sender, e); e.Handled = true; break;
            case Key.Up when ctrl: MoveBy(-1); e.Handled = true; break;
            case Key.Down when ctrl: MoveBy(1); e.Handled = true; break;
            case Key.M when ctrl: MoveTo_Click(sender, e); e.Handled = true; break;
        }
    }

    // ---------- Drag to reorder ----------

    void ChannelGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        _dragStart = e.GetPosition(null);
        _pendingSingleSelect = null;
        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        _dragArmed = row is not null && !IsEditing(grid) && e.ClickCount == 1;
        if (_dragArmed && row!.IsSelected && grid.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            // Keep the multi-selection so it can be dragged; a plain click without a drag selects the row on release.
            _pendingSingleSelect = row;
            e.Handled = true;
            grid.Focus();
        }
    }

    void ChannelGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pendingSingleSelect is { } row)
        {
            var grid = (DataGrid)sender;
            grid.SelectedItems.Clear();
            row.IsSelected = true;
            grid.CurrentCell = new DataGridCellInfo(row.Item, grid.Columns[0]);
        }
        _pendingSingleSelect = null;
        _dragArmed = false;
    }

    void ChannelGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        _dragArmed = false;
        _pendingSingleSelect = null;
        var grid = (DataGrid)sender;
        if (Filtering) { StatusLabel.Text = Loc.Get("Status.ClearSearch"); return; }
        var picked = Selected();
        if (picked.Count == 0) return;
        CommitEdits();

        _insertion = new InsertionAdorner(grid);
        AdornerLayer.GetAdornerLayer(grid)?.Add(_insertion);
        StatusLabel.Text = Loc.Format("Status.Moving", Loc.Channels(picked));
        DragDropEffects result = DragDropEffects.None;
        try
        {
            result = DragDrop.DoDragDrop(grid, new DataObject(typeof(List<ChannelRow>), picked), DragDropEffects.Move);
        }
        finally
        {
            AdornerLayer.GetAdornerLayer(grid)?.Remove(_insertion);
            _insertion = null;
            // A successful drop already wrote its own status line.
            if (result == DragDropEffects.None) UpdateStatus();
        }
    }

    void ChannelGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(typeof(List<ChannelRow>)))
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        var grid = (DataGrid)sender;
        AutoScroll(grid, e.GetPosition(grid).Y);
        var (_, lineY) = DropTarget(grid, e);
        _insertion?.Show(lineY);
    }

    void ChannelGrid_DragLeave(object sender, DragEventArgs e) => _insertion?.Show(null);

    void ChannelGrid_Drop(object sender, DragEventArgs e)
    {
        _insertion?.Show(null);
        if (e.Data.GetData(typeof(List<ChannelRow>)) is not List<ChannelRow> picked || picked.Count == 0) return;
        e.Handled = true;
        var grid = (DataGrid)sender;
        var rows = grid == TvGrid ? _tv : _radio;
        if (picked.Any(r => !rows.Contains(r))) { e.Effects = DragDropEffects.None; return; }
        var (insertAt, _) = DropTarget(grid, e);
        int index = Reorder.FinalIndex(rows, picked, insertAt);
        if (Reorder.IsNoOp(rows, picked, index)) { e.Effects = DragDropEffects.None; return; }
        MoveRows(rows, picked, index);
        StatusLabel.Text = Loc.Format("Status.Moved", Loc.Channels(picked), index + 1);
    }

    /// <summary>Position in the list the drop lands before, and the Y of the line to draw, from the row under the cursor.</summary>
    static (int InsertAt, double? LineY) DropTarget(DataGrid grid, DragEventArgs e)
    {
        var row = FindParent<DataGridRow>(grid.InputHitTest(e.GetPosition(grid)) as DependencyObject);
        if (row is null)
        {
            // Below the last row: drop at the end.
            var last = grid.ItemContainerGenerator.ContainerFromIndex(grid.Items.Count - 1) as DataGridRow;
            double? y = last is null ? null : last.TranslatePoint(new Point(0, last.ActualHeight), grid).Y;
            return (grid.Items.Count, y);
        }
        int index = row.GetIndex();
        bool lowerHalf = e.GetPosition(row).Y > row.ActualHeight / 2;
        double top = row.TranslatePoint(new Point(0, 0), grid).Y;
        return lowerHalf ? (index + 1, top + row.ActualHeight) : (index, top);
    }

    static void AutoScroll(DataGrid grid, double y)
    {
        const double edge = 36;
        if (FindChild<ScrollViewer>(grid) is not { } sv) return;
        if (y < edge + 30) sv.LineUp(); // the header row sits at the top
        else if (y > grid.ActualHeight - edge) sv.LineDown();
    }

    // ---------- Search ----------

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = SearchBox.Text.Trim();
        foreach (var grid in new[] { TvGrid, RadioGrid })
        {
            var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
            view.Filter = text.Length == 0
                ? null
                : o => ((ChannelRow)o).Name.Contains(text, StringComparison.CurrentCultureIgnoreCase);
        }
        UpdateStatus();
    }

    void Find_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (ActiveGrid is null) Tabs.SelectedItem = TvTab;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    // ---------- Chrome ----------

    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs) return;
        ChannelBar.Visibility = ActiveGrid is null ? Visibility.Collapsed : Visibility.Visible;
        if (Tabs.SelectedItem == TpTab) RefreshTransponderCounts();
        UpdateStatus();
    }

    void RefreshTransponderCounts()
    {
        if (TpGrid.ItemsSource is not List<TransponderRow> tps) return;
        var counts = _tv.Concat(_radio).GroupBy(r => r.Channel.TransponderIndex).ToDictionary(g => (int)g.Key, g => g.Count());
        foreach (var tp in tps)
        {
            tp.Channels = counts.GetValueOrDefault(tp.Index);
            tp.Refresh();
        }
    }

    void MarkDirty()
    {
        if (_loading) return;
        _dirty = true;
        // Transponder edits show in the channel list.
        if (Tabs.SelectedItem == TpTab || Tabs.SelectedItem == SatTab)
            foreach (var r in _tv.Concat(_radio)) r.Refresh();
        UpdateChrome();
    }

    void UpdateChrome()
    {
        bool open = _db is not null;
        EmptyState.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        Editor.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        FormatBox.IsEnabled = open;
        DirtyLabel.Visibility = _dirty ? Visibility.Visible : Visibility.Collapsed;
        var name = _path is null ? null : Path.GetFileName(_path);
        Title = name is null ? "Sunplus Channel Studio" : $"{name}{(_dirty ? " *" : "")} - Sunplus Channel Studio";
        FileLabel.Text = name is null ? string.Empty : $"{name} ({_db!.Type.Label()})";
        FileLabel.ToolTip = _path;
        TvTab.Header = Loc.Format("Tab.Tv", _tv.Count);
        RadioTab.Header = Loc.Format("Tab.Radio", _radio.Count);
        TpTab.Header = Loc.Format("Tab.Tp", _db?.Transponders.Count ?? 0);
        SatTab.Header = Loc.Format("Tab.Sat", _db?.Satellites.Count ?? 0);
        CommandManager.InvalidateRequerySuggested();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (_db is null) { StatusLabel.Text = Loc.Get("Status.Ready"); return; }
        var grid = ActiveGrid;
        if (grid is null)
        {
            StatusLabel.Text = Loc.Format("Status.Database", _db.Type.Label());
            return;
        }
        int shown = ((ICollectionView)CollectionViewSource.GetDefaultView(grid.ItemsSource)).Cast<object>().Count();
        int total = ActiveRows!.Count;
        int selected = grid.SelectedItems.Count;
        var parts = new List<string> { Filtering ? Loc.Format("Status.Shown", shown, total) : Loc.Format("Status.Channels", total) };
        if (selected > 0) parts.Add(Loc.Format("Status.Selected", selected));
        StatusLabel.Text = string.Join(Loc.Get("List.Separator"), parts) + ".";
    }

    // ---------- Language ----------

    void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            FlowDirection = Loc.Flow,
            PlacementTarget = LanguageButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var language in Loc.Languages)
        {
            var item = new MenuItem
            {
                Header = language.NativeName,
                Icon = LanguageIcon(language),
                IsCheckable = true,
                IsChecked = language == Loc.Current,
            };
            item.Click += (_, _) => { if (language != Loc.Current) Loc.Apply(language.Code); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    static Image LanguageIcon(UiLanguage language) =>
        new() { Source = new System.Windows.Media.Imaging.BitmapImage(language.Icon), Width = 18, Height = 18 };

    void ShowCurrentLanguage()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(LanguageIcon(Loc.Current));
        panel.Children.Add(new TextBlock { Text = Loc.Current.NativeName, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        LanguageButton.Content = panel;
    }

    /// <summary>Re-translates text set in code; XAML text follows the swapped dictionary by itself.</summary>
    void ApplyLanguage()
    {
        FlowDirection = Loc.Flow;
        ShowCurrentLanguage();
        foreach (var (col, key) in _headerKeys) col.Header = Loc.Get(key);
        foreach (var r in _tv.Concat(_radio)) r.Refresh();
        foreach (var r in (TpGrid.ItemsSource as IEnumerable<Row> ?? []).Concat(SatGrid.ItemsSource as IEnumerable<Row> ?? [])) r.Refresh();
        UpdateChrome();
    }

    MessageBoxResult Say(string text, string titleKey, MessageBoxButton buttons, MessageBoxImage icon) =>
        MessageBox.Show(this, text, Loc.Get(titleKey), buttons, icon, MessageBoxResult.None, Loc.BoxOptions);

    // ---------- Helpers ----------

    static void Renumber(ObservableCollection<ChannelRow> rows)
    {
        for (int i = 0; i < rows.Count; i++) rows[i].Number = i + 1;
    }

    void CommitEdits()
    {
        foreach (var grid in new[] { TvGrid, RadioGrid, TpGrid, SatGrid })
            grid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    static bool IsEditing(DataGrid grid) =>
        grid.CurrentCell.Column is not null && Keyboard.FocusedElement is TextBox { IsReadOnly: false } box
        && FindParent<DataGrid>(box) == grid;

    static T? FindChild<T>(DependencyObject node) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T hit) return hit;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        return node as T;
    }
}
