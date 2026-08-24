using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KidTime.Domain.Contracts;

namespace KidTime.Setup;

public partial class SetupWindow
{
    private const int ConnectionStepIndex = 0;
    private const int AccountStepIndex = 1;
    private const int ConnectStepIndex = 2;

    private readonly StepIndicator[] _stepIndicators;
    private LocalAccount? _childAccount;
    private int _step;
    private bool _installing;
    private bool _finished;
    private bool _alreadyConnected;
    private bool _applyingSetupCode;

    public SetupWindow()
    {
        InitializeComponent();
        _stepIndicators =
        [
            new StepIndicator(Step1Circle, Step1Number, Step1Check, Step1Label),
            new StepIndicator(Step2Circle, Step2Number, Step2Check, Step2Label),
            new StepIndicator(Step3Circle, Step3Number, Step3Check, Step3Label)
        ];
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SummaryComputer.Text = Environment.MachineName;
        SummaryWindows.Text = Environment.OSVersion.VersionString;
        if (File.Exists(InstallerEngine.CredentialPath))
        {
            _alreadyConnected = true;
            ShowInfo(
                ConnectionInfo,
                Wpf.Ui.Controls.InfoBarSeverity.Error,
                "This PC is already connected",
                "Manage it from the KidTime web panel, or remove KidTime from this PC first, then run setup again.");
        }

        GoToStep(ConnectionStepIndex);
        ServerUrlBox.Focus();
        await LoadAccountsAsync();
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_applyingSetupCode) return;
        ApplySetupCodeIfPasted();
        UpdateValidation();
    }

    /// <summary>
    /// A setup code carries the server URL as well, so pasting one fills both required options.
    /// </summary>
    private void ApplySetupCodeIfPasted()
    {
        if (!SetupCode.TryParse(EnrollmentCodeBox.Text, out var serverUrl, out var enrollmentCode)) return;
        _applyingSetupCode = true;
        ServerUrlBox.Text = serverUrl;
        EnrollmentCodeBox.Text = enrollmentCode;
        EnrollmentCodeBox.CaretIndex = enrollmentCode.Length;
        _applyingSetupCode = false;
        if (_alreadyConnected) return;
        ShowInfo(
            ConnectionInfo,
            Wpf.Ui.Controls.InfoBarSeverity.Success,
            "Setup code applied",
            "The server URL and the enrollment code were filled in for you.");
    }

    private void UpdateValidation()
    {
        var url = ServerUrlBox.Text.Trim();
        SetError(
            ServerUrlError,
            url.Length > 0 && !TryGetServerUrl(out _)
                ? "Enter the full HTTPS address shown by Add device, for example https://parent-pc:5081."
                : null);

        var code = EnrollmentCodeBox.Text.Trim();
        SetError(
            EnrollmentCodeError,
            code.Length > 0 && !TryGetEnrollmentCode(out _)
                ? "This code is incomplete. Copy it again from Add device; it expires after 30 minutes."
                : null);

        UpdateNavigation();
    }

    private bool TryGetServerUrl(out string serverUrl)
    {
        serverUrl = string.Empty;
        if (!Uri.TryCreate(ServerUrlBox.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        serverUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return serverUrl.Length > 0;
    }

    private bool TryGetEnrollmentCode(out string enrollmentCode)
    {
        enrollmentCode = EnrollmentCodeBox.Text.Trim();
        return EnrollmentCode.TryParse(enrollmentCode, out var token, out _) && token.Length >= 20;
    }

    private async Task LoadAccountsAsync()
    {
        RefreshAccountsButton.IsEnabled = false;
        _childAccount = null;
        try
        {
            var accounts = await Task.Run(LocalAccountDiscovery.GetStandardUsers);
            AccountItems.ItemsSource = accounts;
            if (accounts.Count == 0)
                ShowInfo(
                    AccountsInfo,
                    Wpf.Ui.Controls.InfoBarSeverity.Warning,
                    "No standard account was found",
                    "Create a standard (non-administrator) Windows account for the child in Settings, then select Refresh.");
            else
                AccountsInfo.IsOpen = false;
        }
        catch (Exception exception)
        {
            AccountItems.ItemsSource = Array.Empty<LocalAccount>();
            ShowInfo(
                AccountsInfo,
                Wpf.Ui.Controls.InfoBarSeverity.Error,
                "Windows accounts could not be listed",
                exception.Message);
        }
        finally
        {
            RefreshAccountsButton.IsEnabled = true;
            UpdateNavigation();
        }
    }

    private async void RefreshAccountsButton_Click(object sender, RoutedEventArgs e) => await LoadAccountsAsync();

    private void Account_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: LocalAccount account }) _childAccount = account;
        UpdateNavigation();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_step > ConnectionStepIndex) GoToStep(_step - 1);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanLeaveCurrentStep()) return;
        if (_step < ConnectStepIndex)
        {
            GoToStep(_step + 1);
            return;
        }

        await ConnectAsync();
    }

    private void FinishButton_Click(object sender, RoutedEventArgs e) => Close();

    private bool CanLeaveCurrentStep() => !_alreadyConnected && !_installing && _step switch
    {
        ConnectionStepIndex => TryGetServerUrl(out _) && TryGetEnrollmentCode(out _),
        AccountStepIndex => _childAccount is not null,
        _ => _childAccount is not null && TryGetServerUrl(out _) && TryGetEnrollmentCode(out _)
    };

    private void GoToStep(int step)
    {
        _step = step;
        ConnectionStep.Visibility = step == ConnectionStepIndex ? Visibility.Visible : Visibility.Collapsed;
        AccountStep.Visibility = step == AccountStepIndex ? Visibility.Visible : Visibility.Collapsed;
        ConnectStep.Visibility = step == ConnectStepIndex ? Visibility.Visible : Visibility.Collapsed;
        if (step == ConnectStepIndex)
        {
            SummaryServer.Text = TryGetServerUrl(out var serverUrl) ? serverUrl : ServerUrlBox.Text.Trim();
            SummaryAccount.Text = _childAccount?.Label ?? string.Empty;
        }

        UpdateStepper();
        UpdateNavigation();
    }

    private void UpdateNavigation()
    {
        if (_finished) return;
        BackButton.IsEnabled = _step > ConnectionStepIndex && !_installing;
        NextButton.IsEnabled = CanLeaveCurrentStep();
        NextButton.Content = _step == ConnectStepIndex ? "Connect" : "Next";
        NextButton.Icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = _step == ConnectStepIndex
                ? Wpf.Ui.Controls.SymbolRegular.PlugConnected24
                : Wpf.Ui.Controls.SymbolRegular.ArrowRight24
        };
        if (_installing) return;
        StatusText.Text = _alreadyConnected
            ? "KidTime is already installed on this PC"
            : _step switch
            {
                ConnectionStepIndex => "Step 1 of 3 · Enter the server URL and enrollment code",
                AccountStepIndex => "Step 2 of 3 · Choose the child's Windows account",
                _ => NextButton.IsEnabled ? "Step 3 of 3 · Ready to connect" : "Step 3 of 3 · Complete the earlier steps first"
            };
    }

    private void UpdateStepper()
    {
        var onAccentText = (Brush)FindResource("TextOnAccentFillColorPrimaryBrush");
        var pendingText = (Brush)FindResource("TextFillColorSecondaryBrush");
        for (var index = 0; index < _stepIndicators.Length; index++)
        {
            var indicator = _stepIndicators[index];
            var done = _finished || index < _step;
            var active = !_finished && index == _step;
            indicator.Circle.Style = (Style)FindResource(done ? "StepCircleDone" : active ? "StepCircleActive" : "StepCirclePending");
            indicator.Number.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
            indicator.Number.Foreground = active ? onAccentText : pendingText;
            indicator.Check.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
            indicator.Label.Style = (Style)FindResource(done || active ? "StepLabelActive" : "StepLabelPending");
        }

        var accent = (Brush)FindResource("SystemAccentColorTertiaryBrush");
        var line = (Brush)FindResource("ControlStrokeColorDefaultBrush");
        Connector1.Fill = _finished || _step >= AccountStepIndex ? accent : line;
        Connector2.Fill = _finished || _step >= ConnectStepIndex ? accent : line;
    }

    private async Task ConnectAsync()
    {
        if (!TryGetServerUrl(out var serverUrl) || !TryGetEnrollmentCode(out var enrollmentCode) || _childAccount is null) return;

        _installing = true;
        ConnectError.IsOpen = false;
        InstallProgress.IsIndeterminate = false;
        InstallProgress.Value = 0;
        InstallProgress.Visibility = Visibility.Visible;
        UpdateNavigation();
        try
        {
            var progress = new Progress<InstallationProgress>(update =>
            {
                StatusText.Text = update.Message;
                InstallProgress.Value = update.Percent;
            });
            await InstallerEngine.InstallAsync(
                new InstallationRequest(serverUrl, enrollmentCode, _childAccount),
                progress,
                CancellationToken.None);
            ShowCompleted();
        }
        catch (Exception exception)
        {
            ConnectError.Message = exception.Message;
            ConnectError.IsOpen = true;
            StatusText.Text = "Setup could not connect this PC";
            InstallProgress.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _installing = false;
            UpdateNavigation();
        }
    }

    private void ShowCompleted()
    {
        _finished = true;
        ConnectionStep.Visibility = Visibility.Collapsed;
        AccountStep.Visibility = Visibility.Collapsed;
        ConnectStep.Visibility = Visibility.Collapsed;
        DoneStep.Visibility = Visibility.Visible;
        DoneDetail.Text = $"KidTime is running and now controls {_childAccount?.AccountName}. The parent web panel shows this PC as connected within a few seconds.";
        StatusText.Text = "Setup finished";
        InstallProgress.Value = 100;
        InstallProgress.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Collapsed;
        FinishButton.Visibility = Visibility.Visible;
        FinishButton.Focus();
        UpdateStepper();
    }

    private static void SetError(TextBlock target, string? message)
    {
        target.Text = message ?? string.Empty;
        target.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void ShowInfo(Wpf.Ui.Controls.InfoBar target, Wpf.Ui.Controls.InfoBarSeverity severity, string title, string message)
    {
        target.Severity = severity;
        target.Title = title;
        target.Message = message;
        target.IsOpen = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_installing)
        {
            e.Cancel = true;
            StatusText.Text = "Setup is still installing. Please wait for it to finish.";
            return;
        }

        base.OnClosing(e);
    }

    private sealed record StepIndicator(
        Border Circle,
        TextBlock Number,
        Wpf.Ui.Controls.SymbolIcon Check,
        TextBlock Label);
}
