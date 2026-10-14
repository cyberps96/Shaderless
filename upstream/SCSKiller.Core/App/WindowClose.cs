namespace SCSKiller.Core.App;

public enum CloseAction { Quit, Hide, Close }

/// <summary>The window's close button (and Alt+F4): the tray's Quit with <see cref="Settings.CloseQuits"/> on, else hide to
/// the notification area while the tray icon is there. Once quitting, never Quit again: a second Quit ends the driver's
/// cache write ("Quit now").</summary>
public static class WindowClose
{
    public static CloseAction Of(Settings s, bool quitting, bool trayAdded) =>
        s.CloseQuits && !quitting ? CloseAction.Quit : trayAdded ? CloseAction.Hide : CloseAction.Close;
}
