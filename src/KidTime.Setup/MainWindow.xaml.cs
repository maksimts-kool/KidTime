using KidTime.Domain.Contracts;

namespace KidTime.Setup;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Finding Standard User accounts…";
            var accounts = await Task.Run(LocalAccountDiscovery.GetStandardUsers);
            ChildUserBox.ItemsSource = accounts;
            ChildUserBox.SelectedIndex = accounts.Count > 0 ? 0 : -1;
            StatusText.Text = accounts.Count > 0 ? "Ready to connect" : "No eligible child user was found";
            if (accounts.Count == 0)
                ShowMessage("Create a Standard User account for the child in Windows Settings, then reopen KidTime Setup.");
        }
        catch (Exception exception)
        {
            StatusText.Text = "Could not read Windows users";
            ShowMessage($"Windows user accounts could not be loaded. {exception.Message}");
        }
    }

    private async void ConnectButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        MessageBorder.Visibility = System.Windows.Visibility.Collapsed;
        if (!Uri.TryCreate(ServerUrlBox.Text.Trim(), UriKind.Absolute, out var serverUri)
            || serverUri.Scheme != Uri.UriSchemeHttps)
        {
            ShowMessage("Enter the HTTPS server URL shown in the KidTime web panel.");
            return;
        }

        if (!EnrollmentCode.TryParse(EnrollmentTokenBox.Text, out _, out _))
        {
            ShowMessage("Paste the complete enrollment token from the KidTime web panel.");
            return;
        }

        if (ChildUserBox.SelectedItem is not LocalAccount account)
        {
            ShowMessage("Choose the child’s Standard User account.");
            return;
        }

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            await InstallerEngine.InstallAsync(
                new InstallationRequest(serverUri.ToString().TrimEnd('/'), EnrollmentTokenBox.Text.Trim(), account),
                progress,
                CancellationToken.None);
            StatusText.Text = "Installation complete";
            SuccessText.Text = $"KidTime is running and will control {account.AccountName}. You can return to the parent web panel now.";
            SuccessPanel.Visibility = System.Windows.Visibility.Visible;
            ServerUrlBox.IsEnabled = false;
            EnrollmentTokenBox.IsEnabled = false;
            ChildUserBox.IsEnabled = false;
            ConnectButton.Visibility = System.Windows.Visibility.Collapsed;
            CloseButton.Visibility = System.Windows.Visibility.Visible;
        }
        catch (Exception exception)
        {
            StatusText.Text = "Could not connect this PC";
            ShowMessage(exception.Message);
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        ConnectButton.IsEnabled = !busy;
        ServerUrlBox.IsEnabled = !busy;
        EnrollmentTokenBox.IsEnabled = !busy;
        ChildUserBox.IsEnabled = !busy;
        InstallProgress.Visibility = busy ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessageBorder.Visibility = System.Windows.Visibility.Visible;
    }

    private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
}
