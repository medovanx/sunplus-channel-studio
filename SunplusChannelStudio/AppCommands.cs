using System.Windows.Input;

namespace SunplusChannelStudio;

public static class AppCommands
{
    /// <summary>Back to the file as last opened or saved.</summary>
    public static readonly RoutedUICommand Revert = new("Revert", nameof(Revert), typeof(AppCommands));
}
