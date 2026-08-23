using System.Diagnostics;
using System.Drawing;
using System.Media;
using System.Runtime.InteropServices;

namespace KidTime.SessionAgent;

internal sealed class PersistentWarningBanner : Form
{
    private const int NoActivate = 0x08000000;
    private const int ToolWindow = 0x00000080;
    private const int CardWidth = 520;
    private const int CardHeight = 172;
    private const int CardGap = 10;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoActivatePosition = 0x0010;
    private static readonly IntPtr TopMostWindow = new(-1);
    private static PersistentWarningBanner? _host;

    private readonly Dictionary<string, WarningCard> _cards = new(StringComparer.Ordinal);
    private readonly System.Windows.Forms.Timer _topmostTimer = new() { Interval = 1_000 };

    private PersistentWarningBanner()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(36, 31, 25);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ClientSize = new Size(CardWidth, CardHeight);
        _topmostTimer.Tick += (_, _) => EnsureTopmost();
        _topmostTimer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NoActivate | ToolWindow;
            return parameters;
        }
    }

    public static void Display(string warningKey, string title, string message, int countdownSeconds)
    {
        var host = GetOrCreateHost();
        if (host._cards.ContainsKey(warningKey))
        {
            SessionLogger.Information($"Duplicate persistent final warning suppressed: {warningKey}");
            return;
        }

        var card = new WarningCard(warningKey, title, message, countdownSeconds);
        card.Expired += host.CardExpired;
        host._cards.Add(warningKey, card);
        host.Controls.Add(card);
        host.LayoutCards();
        if (!host.Visible) host.Show();
        host.Refresh();
        host.EnsureTopmost();
        SystemSounds.Exclamation.Play();
        SessionLogger.Information($"Persistent final warning shown: {title}");
    }

    public static void Dismiss(string warningKey)
    {
        if (_host is null || !_host._cards.TryGetValue(warningKey, out var card)) return;
        _host.RemoveCard(card);
        SessionLogger.Information($"Persistent final warning dismissed: {warningKey}");
    }

    private static PersistentWarningBanner GetOrCreateHost()
    {
        if (_host is null || _host.IsDisposed) _host = new PersistentWarningBanner();
        return _host;
    }

    private void CardExpired(object? sender, EventArgs e)
    {
        if (sender is WarningCard card) RemoveCard(card);
    }

    private void RemoveCard(WarningCard card)
    {
        card.Expired -= CardExpired;
        _cards.Remove(card.WarningKey);
        Controls.Remove(card);
        card.Dispose();
        if (_cards.Count == 0)
        {
            Hide();
            return;
        }
        LayoutCards();
        Refresh();
    }

    private void LayoutCards()
    {
        var index = 0;
        foreach (var card in _cards.Values)
        {
            card.Location = new Point(0, index * (CardHeight + CardGap));
            index++;
        }

        ClientSize = new Size(CardWidth, _cards.Count * CardHeight + Math.Max(0, _cards.Count - 1) * CardGap);
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(workingArea.Right - Width - 20, workingArea.Top + 20);
    }

    private void EnsureTopmost()
    {
        if (!Visible || !IsHandleCreated) return;
        _ = SetWindowPos(
            Handle,
            TopMostWindow,
            0,
            0,
            0,
            0,
            NoMove | NoSize | NoActivatePosition);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _topmostTimer.Stop();
        _topmostTimer.Dispose();
        foreach (var card in _cards.Values) card.Dispose();
        _cards.Clear();
        if (ReferenceEquals(_host, this)) _host = null;
        base.OnFormClosed(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private sealed class WarningCard : Panel
    {
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly Label _countdownLabel;
        private readonly int _countdownSeconds;

        public WarningCard(string warningKey, string title, string message, int countdownSeconds)
        {
            WarningKey = warningKey;
            _countdownSeconds = Math.Max(1, countdownSeconds);
            BackColor = Color.FromArgb(36, 31, 25);
            ForeColor = Color.White;
            Size = new Size(CardWidth, CardHeight);
            var fontFamily = SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif;

            Controls.Add(new Panel
            {
                BackColor = Color.FromArgb(245, 158, 11),
                Location = new Point(18, 18),
                Size = new Size(7, CardHeight - 36),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left
            });

            var layout = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 3,
                Location = new Point(35, 18),
                Size = new Size(CardWidth - 53, CardHeight - 36),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

            layout.Controls.Add(new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Font = new Font(fontFamily, 13, FontStyle.Bold),
                Text = title,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);

            _countdownLabel = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font(fontFamily, 16, FontStyle.Bold),
                ForeColor = Color.FromArgb(253, 186, 116),
                TextAlign = ContentAlignment.MiddleRight
            };
            layout.Controls.Add(_countdownLabel, 1, 0);

            var messageLabel = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Font = new Font(fontFamily, 10),
                Text = message,
                TextAlign = ContentAlignment.MiddleLeft
            };
            layout.SetColumnSpan(messageLabel, 2);
            layout.Controls.Add(messageLabel, 0, 1);

            var footerLabel = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font(fontFamily, 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(253, 186, 116),
                Text = "Save your work now. This final warning stays visible.",
                TextAlign = ContentAlignment.BottomLeft
            };
            layout.SetColumnSpan(footerLabel, 2);
            layout.Controls.Add(footerLabel, 0, 2);
            Controls.Add(layout);

            UpdateCountdown();
            _timer.Tick += TimerTick;
            _timer.Start();
        }

        public string WarningKey { get; }
        public event EventHandler? Expired;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
            }
            base.Dispose(disposing);
        }

        private void TimerTick(object? sender, EventArgs e)
        {
            if (_stopwatch.Elapsed.TotalSeconds >= _countdownSeconds)
            {
                _timer.Stop();
                Expired?.Invoke(this, EventArgs.Empty);
                return;
            }
            UpdateCountdown();
        }

        private void UpdateCountdown()
        {
            var remaining = Math.Max(0, _countdownSeconds - (int)Math.Floor(_stopwatch.Elapsed.TotalSeconds));
            _countdownLabel.Text = $"{remaining / 60:00}:{remaining % 60:00}";
        }
    }
}
