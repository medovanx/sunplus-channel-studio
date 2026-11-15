using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SunplusChannelStudio.Core;
using SunplusChannelStudio.Core.Import;
using SunplusChannelStudio.Core.Streams;

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
    Task<StreamCatalog>? _catalog;

    // Undo history: the whole database after each edit. A database is about 100 KB, so this stays small.
    const int HistoryLimit = 200;
    readonly List<byte[]> _history = [];
    int _historyIndex = -1;
    byte[]? _savedBytes;
    bool _snapshotQueued;

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
            grid.SelectionChanged += (_, _) => { UpdateStatus(); UpdateWatchAvailability(); };
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
        // A number typed in the # column moves the channel once the edit has finished.
        ChannelRow.MoveRequested += (row, target) => Dispatcher.BeginInvoke(() => MoveToNumber(row, target));
        Loaded += (_, _) =>
        {
            // SunplusChannelStudio.exe [database.bin] [--import list]
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            int imp = args.IndexOf("--import");
            string? list = imp >= 0 && imp + 1 < args.Count ? args[imp + 1] : null;
            if (imp >= 0) args.RemoveRange(imp, Math.Min(2, args.Count - imp));
            bool cleanup = args.Remove("--cleanup");
            bool sort = args.Remove("--sort");
            int w = args.IndexOf("--watch");
            string? watch = w >= 0 && w + 1 < args.Count ? args[w + 1] : null;
            if (w >= 0) args.RemoveRange(w, Math.Min(2, args.Count - w));
            if (args.FirstOrDefault() is { } db && File.Exists(db)) OpenFile(db);
            if (list is not null && File.Exists(list) && _db is not null) ImportFile(list);
            if (watch is not null && _tv.FirstOrDefault(r => r.Name.Contains(watch, StringComparison.OrdinalIgnoreCase)) is { } row)
            {
                TvGrid.SelectedItem = row;
                Watch_Click(this, new RoutedEventArgs());
            }
            if (sort && _db is not null) Sort_Click(this, new RoutedEventArgs());
            if (cleanup && _db is not null) Cleanup_Click(this, new RoutedEventArgs());
        };
        UpdateChrome();
    }

    // ---------- Columns ----------

    static void BuildChannelColumns(DataGrid grid)
    {
        DataGridTextColumn Text(string header, string path, double width, bool readOnly = true, bool right = false)
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
                // 0 = the stretching column; anything else sizes to its header and content.
                Width = width > 0 ? DataGridLength.Auto : new DataGridLength(1, DataGridLengthUnitType.Star),
            };
            col.ElementStyle = CellStyle(right);
            if (right) NumericColumns.Add(col);
            return col;
        }

        grid.Columns.Add(Text("Col.Number", nameof(ChannelRow.Position), 56, readOnly: false, right: true));
        var name = Text("Col.Name", nameof(ChannelRow.Name), 0, readOnly: false);
        name.MinWidth = 200;
        grid.Columns.Add(name);
        var live = Text("Col.Live", nameof(ChannelRow.Live), 44);
        var liveStyle = CellStyle(right: false);
        liveStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")));
        liveStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("AccentTextFillColorPrimaryBrush")));
        liveStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
        live.ElementStyle = liveStyle;
        grid.Columns.Add(live);
        grid.Columns.Add(Text("Col.Category", nameof(ChannelRow.Category), 110));
        grid.Columns.Add(Text("Col.Quality", nameof(ChannelRow.Quality), 70));
        grid.Columns.Add(Text("Col.Access", nameof(ChannelRow.Access), 92));
        grid.Columns.Add(Text("Col.Favorite", nameof(ChannelRow.Favorite), 92));
        grid.Columns.Add(Text("Col.Frequency", nameof(ChannelRow.Frequency), 92, right: true));
        grid.Columns.Add(Text("Col.Polarization", nameof(ChannelRow.Polarization), 98));
        grid.Columns.Add(Text("Col.SymbolRate", nameof(ChannelRow.SymbolRate), 104, right: true));
        grid.Columns.Add(Text("Col.Satellite", nameof(ChannelRow.Satellite), 110));
        grid.Columns.Add(Text("Col.Sid", nameof(ChannelRow.ServiceId), 64, right: true));
        grid.Columns.Add(Text("Col.Video", nameof(ChannelRow.VideoPid), 82, right: true));
        grid.Columns.Add(Text("Col.AudioA", nameof(ChannelRow.AudioPidA), 74, right: true));
        grid.Columns.Add(Text("Col.AudioB", nameof(ChannelRow.AudioPidB), 74, right: true));
        grid.Columns.Add(Text("Col.Pcr", nameof(ChannelRow.PcrPid), 62, right: true));
        grid.Columns.Add(Text("Col.Pmt", nameof(ChannelRow.PmtPid), 62, right: true));
    }

    /// <summary>Number columns align to the end of the cell in left-to-right languages only.</summary>
    static readonly HashSet<DataGridColumn> NumericColumns = [];

    static Style CellStyle(bool right)
    {
        var style = new Style(typeof(TextBlock));
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
        ResetHistory();
        RebuildCategorizer();
        PreloadCatalog();
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

    /// <summary>Copies the on-screen order and edits back into the database lists.</summary>
    void SyncToDatabase()
    {
        CommitEdits();
        _db!.TvChannels.Clear();
        _db.TvChannels.AddRange(_tv.Select(r => r.Channel));
        _db.RadioChannels.Clear();
        _db.RadioChannels.AddRange(_radio.Select(r => r.Channel));
        _db.Type = SelectedFormat;
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_db is null) return;
        var dialog = new OpenFileDialog { Filter = Loc.Get("Imp.Filter"), Title = Loc.Get("Imp.Title") };
        if (dialog.ShowDialog(this) == true) ImportFile(dialog.FileName);
    }

    void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (_db is null) return;
        SyncToDatabase();
        var window = new CleanupWindow(_db) { Owner = this };
        if (window.ShowDialog() != true || window.Removed == 0) return;
        _loading = true;
        Fill(_tv, _db.TvChannels);
        Fill(_radio, _db.RadioChannels);
        FillTransponders();
        _loading = false;
        MarkDirty();
        StatusLabel.Text = Loc.Format("Cln.Done", window.Removed);
    }

    // ---------- Sort by category ----------

    void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (_db is null || !CanReorder()) return;
        CommitEdits();
        // If the stream list is still loading, its copy on disk is used so the result never depends on timing.
        RebuildCategorizer(LoadedOrCachedCatalog()); // also picks up edits to the rules file
        var rows = ActiveGrid == RadioGrid ? _radio : _tv;
        var counts = rows.GroupBy(r => r.CategoryValue).ToDictionary(g => g.Key, g => g.Count());
        var window = new SortWindow(counts, hasRadio: rows == _tv && _radio.Count > 0) { Owner = this };
        bool applied = window.ShowDialog() == true;
        RebuildCategorizer(LoadedOrCachedCatalog()); // the rules may have been edited from the Sort window
        if (!applied) return;

        void Apply(ObservableCollection<ChannelRow> list)
        {
            var sorted = Categories.Sort(list, r => r.Name, r => r.CategoryValue, window.Order, window.Alphabetical);
            for (int i = 0; i < sorted.Count; i++)
            {
                int from = list.IndexOf(sorted[i]);
                if (from != i) list.Move(from, i);
            }
            Renumber(list);
        }
        Apply(rows);
        if (window.AlsoRadio) Apply(_radio);
        MarkDirty();
        StatusLabel.Text = Loc.Get("Sort.Done");
    }

    // ---------- Categories ----------

    static string ChoicesPath => Path.Combine(Path.GetDirectoryName(App.ErrorLog)!, "category-choices.json");
    public static string RulesPath => Path.Combine(Path.GetDirectoryName(App.ErrorLog)!, "category-rules.json");

    Dictionary<string, ChannelCategory>? _choices;

    /// <summary>Categories the user set by hand, keyed by the channel's network, stream and service ids.</summary>
    Dictionary<string, ChannelCategory> Choices
    {
        get
        {
            if (_choices is not null) return _choices;
            _choices = [];
            try
            {
                if (File.Exists(ChoicesPath)
                    && System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ChoicesPath)) is { } saved)
                    foreach (var (key, value) in saved)
                        if (Enum.TryParse<ChannelCategory>(value, out var c)) _choices[key] = c;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
            return _choices;
        }
    }

    void SaveChoices()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ChoicesPath)!);
            File.WriteAllText(ChoicesPath, System.Text.Json.JsonSerializer.Serialize(
                Choices.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }

    /// <summary>The user's own rules file when there is one and it reads cleanly, else the shipped rules.</summary>
    CategoryRules LoadRules()
    {
        if (!File.Exists(RulesPath)) return CategoryRules.Default;
        try { return CategoryRules.Parse(File.ReadAllText(RulesPath)); }
        catch (Exception ex) when (ex is CategoryRulesException or IOException)
        {
            StatusLabel.Text = Loc.Format("Cat.RulesError", ex.Message);
            return CategoryRules.Default;
        }
    }

    StreamCatalog? _cachedCatalog;

    /// <summary>The loaded catalogue, else the copy on disk (read once), else null. Never downloads.</summary>
    StreamCatalog? LoadedOrCachedCatalog() =>
        _catalog is { IsCompletedSuccessfully: true } t ? t.Result : _cachedCatalog ??= CatalogCache.TryLoadCached(CatalogFolder);

    void RebuildCategorizer(StreamCatalog? catalog = null)
    {
        catalog ??= _catalog is { IsCompletedSuccessfully: true } t ? t.Result : _cachedCatalog;
        ChannelRow.Categorizer = new Categorizer(LoadRules(), Choices, catalog);
        foreach (var r in _tv.Concat(_radio)) r.Refresh();
        UpdateWatchAvailability();
    }

    /// <summary>
    /// Watch is offered only for channels with a public stream. Until the stream list has loaded it stays
    /// on, since a click loads the list and says so when there is nothing to watch.
    /// </summary>
    void UpdateWatchAvailability()
    {
        var catalog = ChannelRow.Categorizer.Catalog;
        var row = Selected().FirstOrDefault();
        bool live = row is not null && (catalog is null || catalog.Find(row.Name).Count > 0);
        WatchButton.IsEnabled = live;
        WatchButton.SetResourceReference(ToolTipProperty, live || row is null ? "Tip.Watch" : "Tip.WatchNone");
    }

    void AddCategoryItems(ItemCollection items)
    {
        var picked = Selected();
        var auto = new MenuItem { Header = Loc.Get("Cat.Automatic"), IsCheckable = true,
            IsChecked = picked.Count > 0 && picked.All(r => !ChannelRow.Categorizer.IsChosen(r.Channel)) };
        auto.Click += (_, _) => SetCategory(null);
        items.Add(auto);
        items.Add(new Separator());
        foreach (var c in Enum.GetValues<ChannelCategory>())
        {
            var item = new MenuItem { Header = Loc.Get("Cat." + c), IsCheckable = true,
                IsChecked = picked.Count > 0 && picked.All(r => ChannelRow.Categorizer.IsChosen(r.Channel) && r.CategoryValue == c) };
            item.Click += (_, _) => SetCategory(c);
            items.Add(item);
        }
    }

    /// <summary>Sets (or clears, for automatic) the category of the selected channels; remembered for every list.</summary>
    void SetCategory(ChannelCategory? category)
    {
        foreach (var r in Selected())
        {
            var key = Categorizer.KeyOf(r.Channel);
            if (category is { } c) Choices[key] = c; else Choices.Remove(key);
        }
        SaveChoices();
        RebuildCategorizer();
        StatusLabel.Text = category is { } set ? Loc.Format("Cat.Set", Loc.Get("Cat." + set)) : Loc.Get("Cat.SetAuto");
    }

    // ---------- Watch ----------

    static string CatalogFolder => Path.Combine(Path.GetDirectoryName(App.ErrorLog)!, "iptv");

    /// <summary>
    /// The stream catalogue, from the daily cache or downloaded once. When it arrives the Live icons and
    /// catalogue-based categories are filled in.
    /// </summary>
    Task<StreamCatalog> EnsureCatalog(IProgress<string>? progress = null)
    {
        if (_catalog is { IsFaulted: false, IsCanceled: false }) return _catalog;
        // The client must live until every download finishes, so the load is awaited inside the using.
        _catalog = Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SunplusChannelStudio");
            return await new CatalogCache(CatalogFolder, http).LoadAsync(progress);
        });
        _catalog.ContinueWith(t => RebuildCategorizer(t.Result), CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
        return _catalog;
    }

    /// <summary>Starts the catalogue in the background after a file opens; a failure only leaves the Live column empty.</summary>
    async void PreloadCatalog()
    {
        try { await EnsureCatalog(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException) { }
    }

    async void Watch_Click(object sender, RoutedEventArgs e)
    {
        if (Selected().FirstOrDefault() is not { } row || !WatchButton.IsEnabled) return;
        StatusLabel.Text = Loc.Get("Watch.Loading");
        StreamCatalog catalog;
        try
        {
            catalog = await EnsureCatalog(new Progress<string>(file => StatusLabel.Text = Loc.Format("Watch.Downloading", file)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            UpdateStatus();
            Say(Loc.Format("Watch.Failed", ex.Message), "Watch.Title", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UpdateStatus();
        var streams = catalog.Find(row.Name);
        if (streams.Count == 0)
        {
            Say(Loc.Format("Watch.NoStream", row.Name), "Watch.Title", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new PlayerWindow(row.Name, streams) { Owner = this }.Show();
    }

    static bool IsDatabase(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var magic = new byte[4];
            return f.Read(magic) == 4 && magic.AsSpan().SequenceEqual("db15"u8);
        }
        catch (IOException) { return false; }
    }

    void ImportFile(string path)
    {
        if (_db is null) return;
        ImportedList list;
        try
        {
            // Another channel database merges in; anything else is read as a channel list.
            list = IsDatabase(path)
                ? ImportedList.FromDatabase(ChannelDatabase.Load(path), Path.GetFileName(path))
                : ListReader.Read(path);
        }
        catch (Exception ex) when (ex is ImportFormatException or DatabaseFormatException or IOException or UnauthorizedAccessException)
        {
            Say(ex switch
            {
                ImportFormatException f => Loc.Get("Imp." + f.Problem),
                DatabaseFormatException d => Loc.Get("Fmt." + d.Problem),
                _ => ex.Message,
            }, "Imp.CannotRead",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SyncToDatabase();
        var window = new ImportWindow(_db, list) { Owner = this };
        if (window.ShowDialog() != true || window.Applied == 0) return;

        _loading = true;
        Fill(_tv, _db.TvChannels);
        Fill(_radio, _db.RadioChannels);
        FillTransponders();
        _loading = false;
        MarkDirty();
        StatusLabel.Text = Loc.Format("Imp.Done", window.Applied);
    }

    bool SaveTo(string path)
    {
        if (_db is null) return false;
        SyncToDatabase();
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
        _savedBytes = _db.ToBytes();
        if (_historyIndex < 0 || !_history[_historyIndex].AsSpan().SequenceEqual(_savedBytes))
        {
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add(_savedBytes);
            _historyIndex = _history.Count - 1;
        }
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
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        // A database opens; any other file is read as a channel list to import.
        if (IsDatabase(files[0]) || _db is null) OpenFile(files[0]);
        else ImportFile(files[0]);
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

    void MoveToNumber(ChannelRow row, int target)
    {
        var rows = _tv.Contains(row) ? _tv : _radio.Contains(row) ? _radio : null;
        if (rows is null || !CanReorder()) return;
        MoveRows(rows, [row], Math.Clamp(target, 1, rows.Count) - 1);
        StatusLabel.Text = Loc.Format("Status.Moved", Loc.Channels([row]), row.Number);
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
        menu.Items.OfType<MenuItem>().First(i => (string?)i.Tag == "Watch").IsEnabled = WatchButton.IsEnabled;
        var favorites = menu.Items.OfType<MenuItem>().First(i => (string?)i.Tag == "Favorites");
        favorites.Items.Clear();
        AddFavoriteItems(favorites.Items);
        var categories = menu.Items.OfType<MenuItem>().First(i => (string?)i.Tag == "Categories");
        categories.Items.Clear();
        AddCategoryItems(categories.Items);
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
            case Key.Enter when ctrl: Watch_Click(sender, e); e.Handled = true; break;
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
        UpdateWatchAvailability();
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
        QueueSnapshot();
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
        ImportButton.IsEnabled = open;
        CleanupButton.IsEnabled = open;
        DirtyLabel.Visibility = _dirty ? Visibility.Visible : Visibility.Collapsed;
        var name = _path is null ? null : Path.GetFileName(_path);
        Title = name is null ? "Sunplus Channel Studio" : $"{name}{(_dirty ? " *" : "")} - Sunplus Channel Studio";
        FileLabel.Text = name ?? string.Empty;
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

    // ---------- Undo ----------

    void ResetHistory()
    {
        _history.Clear();
        _savedBytes = _db!.ToBytes();
        _history.Add(_savedBytes);
        _historyIndex = 0;
    }

    /// <summary>Records the state after an edit, once any cell edit in progress has finished.</summary>
    void QueueSnapshot()
    {
        if (_snapshotQueued || _db is null) return;
        _snapshotQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _snapshotQueued = false;
            if (_db is null) return;
            SyncToDatabase();
            var bytes = _db.ToBytes();
            if (_historyIndex >= 0 && bytes.AsSpan().SequenceEqual(_history[_historyIndex])) return;
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1); // drop the redo branch
            _history.Add(bytes);
            if (_history.Count > HistoryLimit) _history.RemoveAt(0);
            _historyIndex = _history.Count - 1;
            CommandManager.InvalidateRequerySuggested();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    void Undo_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _historyIndex > 0;
    void Redo_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _historyIndex >= 0 && _historyIndex < _history.Count - 1;
    void Undo_Executed(object sender, ExecutedRoutedEventArgs e) { if (_historyIndex > 0) Restore(--_historyIndex, "Status.Undone"); }
    void Redo_Executed(object sender, ExecutedRoutedEventArgs e) { if (_historyIndex < _history.Count - 1) Restore(++_historyIndex, "Status.Redone"); }

    void Revert_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _db is not null && _dirty;

    /// <summary>Goes back to the file as last opened or saved. The revert itself can be undone.</summary>
    void Revert_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_savedBytes is null) return;
        if (Say(Loc.Get("Dlg.RevertAsk"), "Dlg.RevertTitle", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        CommitEdits();
        _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(_savedBytes);
        _historyIndex = _history.Count - 1;
        Restore(_historyIndex, "Status.Reverted");
    }

    void Restore(int index, string statusKey)
    {
        CommitEdits();
        _loading = true;
        var db = ChannelDatabase.Parse(_history[index]);
        _db = db;
        Fill(_tv, db.TvChannels);
        Fill(_radio, db.RadioChannels);
        FillTransponders();
        FormatBox.SelectedItem = FormatBox.Items.Cast<ComboBoxItem>().First(i => (DatabaseType)i.Tag == db.Type);
        _loading = false;
        _dirty = _savedBytes is null || !_history[index].AsSpan().SequenceEqual(_savedBytes);
        UpdateChrome();
        StatusLabel.Text = Loc.Get(statusKey);
        CommandManager.InvalidateRequerySuggested();
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
        foreach (var col in NumericColumns.Where(_headerKeys.ContainsKey))
            ((DataGridTextColumn)col).ElementStyle = CellStyle(right: !Loc.IsRightToLeft);
        foreach (var (col, key) in _headerKeys)
        {
            col.Header = Loc.Get(key);
            col.HeaderStyle = HeaderStyle(Loc.Get(key.Replace("Col.", "ColTip.")));
        }
        foreach (var r in _tv.Concat(_radio)) r.Refresh();
        foreach (var r in (TpGrid.ItemsSource as IEnumerable<Row> ?? []).Concat(SatGrid.ItemsSource as IEnumerable<Row> ?? [])) r.Refresh();
        UpdateChrome();
    }

    /// <summary>Column header style carrying an explanation as its tooltip.</summary>
    Style HeaderStyle(string tip)
    {
        var style = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader),
            (Style)FindResource(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)));
        style.Setters.Add(new Setter(ToolTipProperty, tip));
        style.Setters.Add(new Setter(ToolTipService.InitialShowDelayProperty, 300));
        return style;
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
