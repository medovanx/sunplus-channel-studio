using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using SunplusChannelStudio.Core.Streams;

namespace SunplusChannelStudio;

/// <summary>Plays a channel's public live stream, trying the next source when one fails.</summary>
public partial class PlayerWindow : Window
{
    static LibVLC? _vlc;
    readonly MediaPlayer _player;
    readonly IReadOnlyList<LiveStream> _streams;
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

        _vlc ??= CreateVlc();
        _player = new MediaPlayer(_vlc) { Volume = (int)VolumeSlider.Value, Mute = VolumeSlider.Value == 0 };
        _player.Playing += (_, _) => Dispatcher.BeginInvoke(() => Status("Play.Playing"));
        _player.Buffering += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            if (e.Cache < 100) StatusLabel.Text = Loc.Format("Play.Buffering", (int)e.Cache);
        });
        _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(TryNext);
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(TryNext);
        Video.MediaPlayer = _player;

        foreach (var s in streams)
            SourceBox.Items.Add(new ComboBoxItem { Content = Describe(s), ToolTip = s.Url });
        SourceBox.Visibility = SourceLabel.Visibility = streams.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMute();
        Loaded += (_, _) => SourceBox.SelectedIndex = 0;
    }

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

    void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceBox.SelectedIndex >= 0 && SourceBox.SelectedIndex != _current) Play(SourceBox.SelectedIndex);
    }

    void Play(int index)
    {
        _current = index;
        var s = _streams[index];
        Status("Play.Connecting");
        var options = new List<string>();
        if (s.UserAgent is { } ua) options.Add($":http-user-agent={ua}");
        if (s.Referrer is { } r) options.Add($":http-referrer={r}");
        var media = new Media(_vlc!, new Uri(s.Url), options.ToArray());
        // Starting playback blocks briefly while VLC opens the stream, so keep it off the UI thread.
        // The player holds its own reference once playing, so the media is released after Play.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            _player.Play(media);
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
        Status("Play.Unavailable");
    }

    void Status(string key) => StatusLabel.Text = Loc.Get(key);

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
        if (MuteButton is null || _player is null) return;
        double v = VolumeSlider.Value;
        MuteGlyph.Text = v == 0 ? "" : v < 34 ? "" : v < 67 ? "" : "";
        MuteButton.SetResourceReference(ToolTipProperty, v == 0 ? "Play.Unmute" : "Play.Mute");
    }

    void Window_Closed(object? sender, EventArgs e)
    {
        var player = _player;
        Video.MediaPlayer = null;
        // Stopping can block on a slow network, so release the player in the background.
        ThreadPool.QueueUserWorkItem(_ => { player.Stop(); player.Dispose(); });
    }
}
