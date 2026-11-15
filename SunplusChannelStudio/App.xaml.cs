using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SunplusChannelStudio;

public partial class App : Application
{
    /// <summary>Log of unexpected errors, next to the user's other app data.</summary>
    public static string ErrorLog { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SunplusChannelStudio", "errors.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        Loc.Init();
    }

    // An unexpected error is logged and reported; the editor stays open so unsaved work is not lost.
    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLog)!);
            File.AppendAllText(ErrorLog, $"{DateTime.Now:u}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
        MessageBox.Show(Loc.Format("Err.Unexpected", e.Exception.Message, ErrorLog), "Sunplus Channel Studio",
            MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.None, Loc.BoxOptions);
        e.Handled = true;
    }
}
