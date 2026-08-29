using System.Windows;
using System.Windows.Threading;
using KidTime.Domain.Contracts;

namespace KidTime.SessionAgent;

/// <summary>
/// Owns the live countdown cards - at most one per warning key, so a blocked application and a
/// pending sign-out can be on screen together without fighting for the same corner.
///
/// Since the card became the final warning the child actually reads, <see cref="Show"/> reports
/// whether one is really on screen: the caller falls back to the native urgent toast when it is
/// not, so a failure here costs the child a nicer warning, never the warning itself.
///
/// Every card is on a hard leash. One shared one-second timer repaints the visible cards and
/// closes any whose deadline has passed, and the timer stops as soon as the last card is gone,
/// so an idle agent costs nothing. Enforcement never consults this class: the service holds the
/// monotonic deadline and signs out or closes the application whether a card was drawn or not.
/// </summary>
internal sealed class CountdownCard : IDisposable
{
    private const double CardWidth = 380;
    private const double CardMargin = 16;
    private const double CardGap = 10;
    private const double EstimatedCardHeight = 180;

    private readonly Dictionary<string, CountdownCardWindow> _cards = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Action _openExtraTimeRequest;
    private bool _disposed;

    public CountdownCard(Action openExtraTimeRequest)
    {
        _openExtraTimeRequest = openExtraTimeRequest;
        _timer.Tick += OnTick;
    }

    /// <summary>Returns true only when a card is genuinely on screen for this warning.</summary>
    public bool Show(string warningKey, string title, string message, int countdownSeconds, bool canAskForExtraTime)
    {
        if (_disposed) return false;
        try
        {
            // A repeated warning for the same key restarts the clock, so a second blocked launch
            // in the same episode gets its own shorter grace instead of inheriting the first one.
            Dismiss(warningKey);
            var card = new CountdownCardWindow();
            card.ExtraTimeRequested += OnExtraTimeRequested;
            card.SetContent(title, message, countdownSeconds, canAskForExtraTime);
            // Placed before it is shown: a card that appears at the desktop origin and then jumps
            // to the corner is exactly the kind of flicker an urgent warning cannot afford.
            var work = SystemParameters.WorkArea;
            card.Left = Math.Max(work.Left, work.Right - CardWidth - CardMargin);
            card.Top = Math.Max(work.Top, work.Bottom - CardMargin - EstimatedCardHeight);
            // The final height is only known once the window has laid out, and it moves with the
            // language and with how far the message wraps, so the card re-anchors itself instead
            // of trusting the estimate above.
            card.SizeChanged += OnCardSizeChanged;
            _cards[warningKey] = card;
            card.Show();
            if (!card.IsVisible)
            {
                // Nothing threw, yet no window is on screen. The caller has a toast to fall back
                // on, and that is worth more than a card nobody can see.
                Dismiss(warningKey);
                return false;
            }

            Reposition();
            if (!_timer.IsEnabled) _timer.Start();
            PlayWarningSound();
            return true;
        }
        catch (Exception exception)
        {
            // The service still enforces the deadline either way, and the caller now raises the
            // native toast instead, so this is a degraded warning rather than a lost one.
            Dismiss(warningKey);
            SessionLogger.ReportFault(
                DiagnosticSeverities.Warning,
                "The KidTime countdown card could not be shown; the native warning was used instead.",
                exception);
            return false;
        }
    }

    /// <summary>
    /// The card replaced a toast that carried the reminder sound, and a silent warning is easy to
    /// miss with headphones on a game. Focus Assist silences notifications, not an application's
    /// own audio, so this still reaches the child - and a machine set to No Sounds simply stays
    /// quiet, which is why the countdown never depends on it.
    /// </summary>
    private static void PlayWarningSound()
    {
        try
        {
            System.Media.SystemSounds.Exclamation.Play();
        }
        catch (Exception exception)
        {
            SessionLogger.Information("The countdown warning sound could not be played.", exception);
        }
    }

    public void Dismiss(string warningKey)
    {
        if (!_cards.Remove(warningKey, out var card)) return;
        CloseCard(card);
        Reposition();
        if (_cards.Count == 0) _timer.Stop();
    }

    private void OnCardSizeChanged(object? sender, SizeChangedEventArgs e) => Reposition();

    private void OnExtraTimeRequested(object? sender, EventArgs e) => _openExtraTimeRequest();

    private void OnTick(object? sender, EventArgs e)
    {
        var expired = new List<string>();
        foreach (var (key, card) in _cards)
        {
            if (card.RemainingSeconds <= 0) expired.Add(key);
            else card.Tick();
        }

        foreach (var key in expired) Dismiss(key);
    }

    /// <summary>
    /// Stacks the cards up from the bottom-right of the work area, above the taskbar, where a
    /// Windows toast would be. Reading the work area keeps it off a taskbar on any edge.
    ///
    /// The corner is measured from the size the card actually took, never from
    /// <see cref="CardWidth"/>: a themed window can end up wider than it asked for - WPF-UI's
    /// FluentWindow style carries a minimum size - and a card placed from the assumed width
    /// hangs off the screen edge, taking the countdown itself with it. Clamping to the work area
    /// keeps every card fully visible even when they no longer all fit stacked.
    /// </summary>
    private void Reposition()
    {
        var work = SystemParameters.WorkArea;
        var offset = CardMargin;
        foreach (var card in _cards.Values)
        {
            if (!card.IsVisible) continue;
            var width = card.ActualWidth > 0 ? card.ActualWidth : CardWidth;
            var height = card.ActualHeight > 0 ? card.ActualHeight : EstimatedCardHeight;
            card.Left = Math.Max(work.Left, work.Right - width - CardMargin);
            card.Top = Math.Max(work.Top, work.Bottom - offset - height);
            offset += height + CardGap;
        }
    }

    private void CloseCard(CountdownCardWindow card)
    {
        card.SizeChanged -= OnCardSizeChanged;
        card.ExtraTimeRequested -= OnExtraTimeRequested;
        try { card.CloseForExit(); }
        catch (Exception exception) when (exception is InvalidOperationException)
        {
            SessionLogger.Information("A KidTime countdown card was already gone.", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        foreach (var card in _cards.Values) CloseCard(card);
        _cards.Clear();
    }
}
