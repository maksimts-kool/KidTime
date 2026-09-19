using System.Runtime.InteropServices;
using System.Text;
using KidTime.Domain.Contracts;

namespace KidTime.ControlService.Sessions;

/// <summary>
/// The Start menu and desktop entries that let the child open the screen-time window without
/// finding a tray icon first.
///
/// A tray icon is easy to lose: Windows hides icons behind the overflow chevron by default, and a
/// child who has never been shown where it is has no way to reach their own screen time, the Apps
/// tab, the Internet tab, or the button that asks a parent for more minutes. Windows Search finds
/// an entry in the Start menu, so putting one there is what makes KidTime an application the child
/// can open by name rather than a thing that only happens to them.
///
/// **They are written by the service, not by setup.** Setup only ever runs on a PC that is not yet
/// enrolled, so an installation that already exists would never get them, while the service starts
/// after every update and every reboot. That also makes them self-healing: a shortcut deleted by
/// accident comes back, and one left pointing at an old path is rewritten.
///
/// Both folders are the all-users ones, which ordinary users can read and not write, so the child
/// keeps the shortcut and cannot quietly remove it. Writing them needs LocalSystem, which is what
/// this service already is.
/// </summary>
public static class AgentShortcuts
{
    /// <summary>
    /// The name on the shortcut, and therefore what the child types into Windows Search.
    ///
    /// It is deliberately **not** in <c>AgentStrings</c>, although almost everything the child
    /// reads is. It is the product's name rather than a sentence, so there is nothing to
    /// translate; and the file name has to be stable, because the parent can change the PC's
    /// language at any time through an ordinary rule revision - a localized file name would mean
    /// deleting and rewriting the child's desktop icon whenever that happened.
    /// </summary>
    private const string ShortcutName = "KidTime";

    /// <summary>
    /// Tells the agent this is a shortcut asking for the window rather than the service starting
    /// the one supervised copy. Shared with the agent through the domain, because a shortcut
    /// without it would start a second agent that the pipe then refuses.
    /// </summary>
    private const string ShowArgument = SessionAgentArguments.Show;

    /// <summary>
    /// Ensures both shortcuts exist and point at the installed agent. Failures are returned rather
    /// than thrown: a PC whose Start menu could not be written is still fully enforced, and losing
    /// the service over a shortcut would be the far worse outcome.
    /// </summary>
    public static void Ensure(string agentExecutable, ILogger logger)
    {
        if (!File.Exists(agentExecutable))
        {
            logger.LogWarning("The SessionAgent executable was not found, so no shortcuts were written.");
            return;
        }

        foreach (var folder in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            var directory = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                logger.LogWarning("Windows did not provide the {Folder} directory; no shortcut was written there.", folder);
                continue;
            }

            var path = Path.Combine(directory, $"{ShortcutName}.lnk");
            try
            {
                if (IsCurrent(path, agentExecutable)) continue;
                Write(path, agentExecutable);
                logger.LogInformation("Wrote the KidTime shortcut to {Path}.", path);
            }
            catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "The KidTime shortcut at {Path} could not be written.", path);
            }
        }
    }

    /// <summary>
    /// Whether the shortcut already says what it should. Rewriting an identical file on every
    /// service start would change its timestamp for no reason and, on the desktop, make Windows
    /// redraw the icon each time.
    /// </summary>
    private static bool IsCurrent(string path, string agentExecutable)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(260);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var arguments = new StringBuilder(260);
            link.GetArguments(arguments, arguments.Capacity);
            return string.Equals(target.ToString(), agentExecutable, StringComparison.OrdinalIgnoreCase)
                   && arguments.ToString().Contains(ShowArgument, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
        {
            // Unreadable means it is replaced, which is the safe answer for a file the child needs.
            return false;
        }
    }

    private static void Write(string path, string agentExecutable)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(agentExecutable);
        link.SetArguments(ShowArgument);
        link.SetWorkingDirectory(Path.GetDirectoryName(agentExecutable) ?? string.Empty);
        // The executable carries the icon, so the shortcut, the taskbar and Alt+Tab all agree.
        link.SetIconLocation(agentExecutable, 0);
        // A name rather than a sentence, for the same reason the file name is one: this is shown
        // to a child whose language the shortcut cannot know.
        link.SetDescription(ShortcutName);
        ((IPersistFile)link).Save(path, fRemember: true);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, int flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);
        void Resolve(IntPtr window, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
