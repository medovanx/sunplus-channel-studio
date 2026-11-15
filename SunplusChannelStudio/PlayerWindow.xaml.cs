using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using SunplusChannelStudio.Core.Streams;

namespace SunplusChannelStudio;

/// <summary>Plays a channel's public live stream, trying the next source when one fails.</summary>
public partial class PlayerWindow : Window
{
    /// <summary>
    /// VLC is started once, on a background thread: loading it scans its plugins, which would freeze the window.
    /// <see cref="WarmUp"/> starts it when the app opens so the first Watch does not wait.
    /// </summary>
    static readonly Lazy<Task<LibVLC>> Vlc = new(() => Task.Run(CreateVlc));

    public static void WarmUp() => _ = Vlc.Value;

    LibVLC? _vlc;
    MediaPlayer? _player;
    IReadOnlyList<LiveStream> _streams;
    readonly bool _capture = Environment.GetEnvironmentVariable("SCS_CAPTURE") == "1";
    int _current = -1;

    public PlayerWindow(string channelName, IReadOnlyList<LiveStream> streams)
    {
        InitializeComponent();
        FlowDirection = Loc.Flow;
        Title = channelName;
        ChannelLabel.Text = channelName;
        _streams = streams;

        if (_capture)
        {
            // Docs and test runs: off-screen, no focus change, no sound.
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -5000;
            Top = 0;
            VolumeSlider.Value = 0;
        }

        FillSources();
        UpdateMute();
        UpdateFullScreenButton();
        FillQualities();
        Loaded += async (_, _) =>
        {
            Status("Play.Connecting");
            ShowSpinner(true);
            _vlc = await Vlc.Value;
            if (!IsLoaded) return; // closed while VLC was starting
            // VLC must not take the mouse and keys itself, so double-click and F11 reach this window.
            _player = new MediaPlayer(_vlc) { Volume = (int)VolumeSlider.Value, Mute = VolumeSlider.Value == 0, EnableMouseInput = false, EnableKeyInput = false };
            _player.Playing += (_, _) => Dispatcher.BeginInvoke(() => Status("Play.Playing"));
            _player.Buffering += (_, e) => Dispatcher.BeginInvoke(() =>
            {
                ShowSpinner(e.Cache < 100);
                StatusLabel.Text = e.Cache < 100 ? Loc.Format("Play.Buffering", (int)e.Cache) : Loc.Get("Play.Playing");
            });
            _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(TryNext);
            _player.EndReached += (_, _) => Dispatcher.BeginInvoke(TryNext);
            Video.MediaPlayer = _player;
            if (_streams.Count == 0) { ShowSpinner(false); Status("Play.ChooseChannel"); return; } // Live TV waits for a pick
            if (SourceBox.SelectedIndex == 0) Play(0); else SourceBox.SelectedIndex = 0;
        };
    }

    /// <summary>
    /// Live TV: the player with the whole catalogue in a sidebar. Picking a channel there plays it here, so
    /// switching channels never opens another window.
    /// </summary>
    public PlayerWindow(Func<IProgress<string>, Task<StreamCatalog>> catalog) : this(Loc.Get("Live.Title"), [])
    {
        SidebarHost.Visibility = ListButton.Visibility = Visibility.Visible;
        Width = 1280;
        Height = 760;
        Sidebar.ChannelChosen += PlayChannel;
        Loaded += async (_, _) =>
        {
            await Sidebar.LoadAsync(catalog);
            if (!_capture) Sidebar.FocusSearch();
        };
    }

    /// <summary>Switches to another channel in this window.</summary>
    void PlayChannel(string name, IReadOnlyList<LiveStream> streams)
    {
        _streams = streams;
        Title = $"{name} - {Loc.Get("Live.Title")}";
        ChannelLabel.Text = name;
        _current = -1;
        FillSources();
        if (streams.Count == 0) return;
        if (SourceBox.SelectedIndex == 0) Play(0); else SourceBox.SelectedIndex = 0;
    }

    void FillSources()
    {
        SourceBox.Items.Clear();
        foreach (var s in _streams)
            SourceBox.Items.Add(new ComboBoxItem { Content = Describe(s), ToolTip = s.Url });
        SourceBox.Visibility = SourceLabel.Visibility = _streams.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ListButton_Click(object sender, RoutedEventArgs e) =>
        SidebarHost.Visibility = SidebarHost.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    static LibVLC CreateVlc()
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC("--no-video-title-show", "--network-caching=1500");
    }

    static string Describe(LiveStream s)
    {
        var parts = new List<string> { s.Title };
        if (s.Quality is { } q) parts.Add(q);
        parts.Add(s.Host);
        if (s.GeoBlocked) parts.Add(Loc.Get("Play.GeoBlocked"));
        if (s.NotAlwaysOn) parts.Add(Loc.Get("Play.NotAlwaysOn"));
        return string.Join(", ", parts);
    }

    /// <summary>Largest picture heights offered; 0 lets the stream pick its best.</summary>
    static readonly int[] Heights = [0, 1080, 720, 480, 360];

    int HeightCap => QualityBox.SelectedItem is ComboBoxItem { Tag: int h } ? h : 0;

    void FillQualities()
    {
        int saved = int.TryParse(Settings.GetString("playerQuality"), out var h) ? h : 0;
        foreach (var height in Heights)
            QualityBox.Items.Add(new ComboBoxItem { Content = height == 0 ? Loc.Get("Play.QualityAuto") : $"{height}p", Tag = height });
        QualityBox.SelectedIndex = Math.Max(0, Array.IndexOf(Heights, saved));
    }

    /// <summary>A new cap applies by reopening the current source; it is remembered for next time.</summary>
    void QualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        Settings.Set("playerQuality", HeightCap.ToString());
        if (_current >= 0) Play(_current);
    }

    void ShowSpinner(bool show)
    {
        if (show == (Spinner.Visibility == Visibility.Visible)) return;
        Spinner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SpinnerTurn.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, show
            ? new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever }
            : null);
    }

    void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceBox.SelectedIndex >= 0 && SourceBox.SelectedIndex != _current) Play(SourceBox.SelectedIndex);
    }

    void Play(int index)
    {
        if (_vlc is null || _player is not { } player) return; // VLC is still starting; the window plays once it is ready
        _current = index;
        var s = _streams[index];
        Status("Play.Connecting");
        ShowSpinner(true);
        var options = new List<string>();
        // For streams offering several sizes, VLC picks the best one no taller than this.
        if (HeightCap > 0) options.Add($":adaptive-maxheight={HeightCap}");
        if (s.UserAgent is { } ua) options.Add($":http-user-agent={ua}");
        if (s.Referrer is { } r) options.Add($":http-referrer={r}");
        var media = new Media(_vlc, new Uri(s.Url), options.ToArray());
        // Starting playback blocks briefly while VLC opens the stream, so keep it off the UI thread.
        // The player holds its own reference once playing, so the media is released after Play.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            player.Play(media);
            media.Dispose();
        });
    }

    /// <summary>A failed source moves on to the next one; after the last, the window says so.</summary>
    void TryNext()
    {
        if (!IsLoaded) return;
        if (_current + 1 < _streams.Count)
        {
            SourceBox.SelectedIndex = _current + 1;
            return;
        }
        ShowSpinner(false);
        Status("Play.Unavailable");
    }

    void Status(string key) => StatusLabel.Text = Loc.Get(key);

    // ---------- Full screen ----------

    bool _fullScreen;
    Visibility _sidebarBefore = Visibility.Collapsed;
    WindowState _stateBefore;

    /// <summary>
    /// Full screen hides both bars and drops the frame; a borderless maximized window covers the taskbar too.
    /// F11, a double-click on the picture or the button toggle it, and Esc leaves it.
    /// </summary>
    void SetFullScreen(bool on)
    {
        if (on == _fullScreen) return;
        _fullScreen = on;
        TopBar.Visibility = BottomBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on) { _sidebarBefore = SidebarHost.Visibility; SidebarHost.Visibility = Visibility.Collapsed; }
        else SidebarHost.Visibility = _sidebarBefore;
        if (on)
        {
            _stateBefore = WindowState;
            WindowState = WindowState.Normal; // the style only takes full effect when maximizing afresh
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBefore;
        }
        UpdateFullScreenButton();
    }

    void UpdateFullScreenButton()
    {
        FullScreenGlyph.Text = _fullScreen ? "\uE73F" : "\uE740";
        FullScreenButton.SetResourceReference(ToolTipProperty, _fullScreen ? "Play.ExitFullScreen" : "Play.FullScreen");
    }

    void FullScreen_Click(object sender, RoutedEventArgs e) => SetFullScreen(!_fullScreen);

    void Video_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) SetFullScreen(!_fullScreen);
    }

    void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F11) { SetFullScreen(!_fullScreen); e.Handled = true; }
        // Page Up and Page Down change channel in Live TV, like a remote, also in full screen.
        else if (ListButton.Visibility == Visibility.Visible && e.Key is System.Windows.Input.Key.PageUp or System.Windows.Input.Key.PageDown)
        {
            Sidebar.Step(e.Key == System.Windows.Input.Key.PageDown ? 1 : -1);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && _fullScreen) { SetFullScreen(false); e.Handled = true; }
    }

    /// <summary>The level Unmute goes back to; muting only moves the slider to 0.</summary>
    double _unmutedVolume = 80;

    void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is null) return;
        _player.Volume = (int)e.NewValue;
        _player.Mute = e.NewValue == 0;
        UpdateMute();
    }

    void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (VolumeSlider.Value > 0)
        {
            _unmutedVolume = VolumeSlider.Value;
            VolumeSlider.Value = 0;
        }
        else VolumeSlider.Value = _unmutedVolume;
    }

    /// <summary>A speaker that shows the level, crossed out at 0; the tooltip says what a click does.</summary>
    void UpdateMute()
    {
        if (MuteButton is null) return;
        double v = VolumeSlider.Value;
        MuteGlyph.Text = v == 0 ? "" : v < 34 ? "" : v < 67 ? "" : "";
        MuteButton.SetResourceReference(ToolTipProperty, v == 0 ? "Play.Unmute" : "Play.Mute");
    }

    void Window_Closed(object? sender, EventArgs e)
    {
        if (_player is not { } player) return;
        Video.MediaPlayer = null;
        // Stopping can block on a slow network, so release the player in the background.
        ThreadPool.QueueUserWorkItem(_ => { player.Stop(); player.Dispose(); });
    }
}
