using System.Windows;
using System.Windows.Threading;
using KidTime.Domain.Contracts;

namespace KidTime.SessionAgent;

/// <summary>
/// Owns the live countdown cards - at most one per warning key, so a blocked application and a
/// pending sign-out can be on screen together without fighting for the same corner.
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
    private const double EstimatedCardHeight = 150;

    private readonly Dictionary<string, CountdownCardWindow> _cards = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _disposed;

    public CountdownCard() => _timer.Tick += OnTick;

    public void Show(string warningKey, string title, string message, int countdownSeconds)
    {
        if (_disposed) return;
        try
        {
            // A repeated warning for the same key restarts the clock, so a second blocked launch
            // in the same episode gets its own shorter grace instead of inheriting the first one.
            Dismiss(warningKey);
            var card = new CountdownCardWindow();
            card.SetContent(title, message, countdownSeconds);
            // Placed before it is shown: a card that appears at the desktop origin and then jumps
            // to the corner is exactly the kind of flicker an urgent warning cannot afford.
            var work = SystemParameters.WorkArea;
            card.Left = work.Right - CardWidth - CardMargin;
            card.Top = work.Bottom - CardMargin - EstimatedCardHeight;
            _cards[warningKey] = card;
            card.Show();
            Reposition();
            if (!_timer.IsEnabled) _timer.Start();
        }
        catch (Exception exception)
        {
            // The toast has already been shown and the service still enforces the deadline, so a
            // card that fails to appear costs the child nothing but is worth reporting.
            _cards.Remove(warningKey);
            SessionLogger.ReportFault(
                DiagnosticSeverities.Warning,
                "The KidTime countdown card could not be shown.",
                exception);
        }
    }

    public void Dismiss(string warningKey)
    {
        if (!_cards.Remove(warningKey, out var card)) return;
        CloseCard(card);
        Reposition();
        if (_cards.Count == 0) _timer.Stop();
    }

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
    /// </summary>
    private void Reposition()
    {
        var work = SystemParameters.WorkArea;
        var offset = CardMargin;
        foreach (var card in _cards.Values)
        {
            if (!card.IsVisible) continue;
            var height = card.ActualHeight > 0 ? card.ActualHeight : EstimatedCardHeight;
            card.Left = work.Right - CardWidth - CardMargin;
            card.Top = work.Bottom - offset - height;
            offset += height + 10;
        }
    }

    private static void CloseCard(CountdownCardWindow card)
    {
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
