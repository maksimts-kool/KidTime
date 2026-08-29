using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Controls;

namespace KidTime.SessionAgent;

/// <summary>
/// The live countdown that accompanies - never replaces - the urgent Windows toast. A toast can
/// say "closes in 60 seconds" once; it cannot show the seconds draining, and it is easy to miss
/// while a child is heads-down in a game. This card shows the remaining time until the deadline
/// the service already owns.
///
/// It is deliberately not a blocker. It is small and corner-anchored, it never activates itself,
/// it cannot be resized or moved into the way, and dismissing it changes nothing about
/// enforcement. Nothing here can strand it on screen either: it lives in the supervised
/// SessionAgent process, which the service restarts within two seconds if it ever dies.
/// </summary>
public partial class CountdownCardWindow : FluentWindow
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
    private int _totalSeconds = 1;
    private bool _allowClose;

    /// <summary>Raised when the child asks for more time; the host opens the window.</summary>
    public event EventHandler? ExtraTimeRequested;

    public CountdownCardWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>Seconds still on the clock, floored at zero.</summary>
    public int RemainingSeconds => Math.Max(
        0,
        _totalSeconds - (int)Stopwatch.GetElapsedTime(_startedTimestamp).TotalSeconds);

    public void SetContent(string title, string message, int countdownSeconds, bool canAskForExtraTime)
    {
        var text = AgentUi.Text;
        _totalSeconds = Math.Max(1, countdownSeconds);
        TitleText.Text = title;
        MessageText.Text = message;
        CountdownCaption.Text = text.CountdownCardTimeLeft;
        DismissButton.Content = text.CountdownCardDismiss;
        AskForTimeButton.Content = text.ExtraTimeAskButton;
        // Only offered when the service says asking is actually possible for what is closing. A
        // button that answered "you cannot ask for that" would be worse than no button at all.
        AskForTimeButton.Visibility = canAskForExtraTime ? Visibility.Visible : Visibility.Collapsed;
        Tick();
    }

    /// <summary>Repaints the clock. Called once a second by the owner while the card is up.</summary>
    public void Tick()
    {
        var remaining = RemainingSeconds;
        CountdownText.Text = $"{remaining / 60}:{remaining % 60:00}";
        RemainingBar.Value = Math.Clamp(remaining * 100d / _totalSeconds, 0, 100);
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void AskForTimeButton_Click(object sender, RoutedEventArgs e)
    {
        // The warning has been read, and the window is about to take over. Enforcement is
        // untouched either way - the service owns the deadline whether this card is up or not.
        Hide();
        ExtraTimeRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Windows closes on the user's Alt+F4 as well; the owner is the only thing that should
        // destroy the card, so an unexpected close just hides it and is retried on the next tick.
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// WS_EX_NOACTIVATE keeps the card from stealing focus - the message is "save your work now",
    /// so taking the keyboard away from the child would be exactly the wrong thing to do.
    /// WS_EX_TOOLWINDOW keeps it out of Alt+Tab.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            SessionLogger.Information("The countdown card could not be marked non-activating.", exception);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    private static int GetWindowLong(IntPtr window, int index) => (int)GetWindowLongPtr(window, index);

    private static void SetWindowLong(IntPtr window, int index, int value) =>
        SetWindowLongPtr(window, index, new IntPtr(value));
}
