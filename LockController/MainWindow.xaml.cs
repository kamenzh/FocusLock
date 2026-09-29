using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Shared;

namespace LockController;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private HttpClient? client;
    private Task? pollingTask;
    private bool submitting;
    private bool connected;
    private int requestVersion;
    private bool canLock;

    public MainWindow() => InitializeComponent();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = await ControllerSettings.LoadAsync(lifetime.Token);
            client = new HttpClient { BaseAddress = settings.GetBaseAddress(), Timeout = TimeSpan.FromSeconds(3) };
            pollingTask = PollAsync(lifetime.Token);
            await pollingTask;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            MessageText.Text = "Controller configuration error: " + exception.Message;
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            // Avoid showing an older poll response over a newly submitted lock.
            if (!submitting) await RefreshAsync(cancellationToken);
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var version = requestVersion;
        try
        {
            var status = await client!.GetFromJsonAsync<LockStatus>("api/status", cancellationToken)
                ?? throw new JsonException("Empty status response.");
            if (!submitting && version == requestVersion) ShowStatus(status);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested && !submitting && version == requestVersion) ShowDisconnected();
        }
    }

    private void ShowStatus(LockStatus status)
    {
        connected = true;
        ConnectionText.Text = "Connected";
        StateText.Text = status.RecoveryRequired ? "RECOVERY REQUIRED" : status.Locked ? "LOCKED" : "UNLOCKED";
        ModeText.Text = !status.EnforcementEnabled ? "Development · timer only" : status.DryRun
            ? $"Development · DryRun · {status.TargetUsername}" : $"Development · real enforcement · {status.TargetUsername}";
        CountdownPanel.Visibility = status.Locked ? Visibility.Visible : Visibility.Collapsed;
        var time = TimeSpan.FromSeconds(Math.Clamp(status.RemainingSeconds, 0, 43200));
        RemainingText.Text = $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
        UnlockText.Text = status.LockUntilUtc?.ToLocalTime().ToString("f") ?? "";
        MessageText.Text = status.EnforcementMessage ?? (!status.EnforcementEnabled
            ? "Timer simulation only. Windows accounts and sessions are unaffected."
            : status.DryRun ? "Safety validation only. No account or session changes."
            : "Use a disposable Standard test account. Keep an administrator session open for recovery.");
        canLock = !status.RecoveryRequired && (!status.EnforcementEnabled || status.TargetAccountConfigured);
        LockButton.IsEnabled = !submitting && canLock;
    }

    private void ShowDisconnected()
    {
        connected = false;
        canLock = false;
        ConnectionText.Text = "Disconnected";
        StateText.Text = "UNKNOWN";
        CountdownPanel.Visibility = Visibility.Collapsed;
        LockButton.IsEnabled = false;
        MessageText.Text = "Cannot reach the service. Retrying automatically…";
    }

    private void Preset_Click(object sender, RoutedEventArgs e) =>
        MinutesText.Text = (string)((Button)sender).Tag;

    private async void Lock_Click(object sender, RoutedEventArgs e)
    {
        if (submitting || client is null) return;
        if (!int.TryParse(MinutesText.Text, out var minutes) || minutes is < 1 or > 720)
        {
            MessageBox.Show(this, "Enter a whole number from 1 through 720 minutes.", "Invalid duration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        submitting = true;
        requestVersion++;
        LockButton.IsEnabled = false;
        try
        {
            // Refresh the configured target and mode before presenting the loss-of-work warning.
            var current = await client.GetFromJsonAsync<LockStatus>("api/status", lifetime.Token)
                ?? throw new JsonException("Empty status response.");
            ShowStatus(current);
            if (!canLock) return;
            if (MessageBox.Show(this, LockConfirmation.Create(current, minutes), "FocusLock", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            using var response = await client.PostAsJsonAsync("api/lock", new LockRequest(minutes), lifetime.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadFromJsonAsync<ApiResult>(lifetime.Token);
                MessageText.Text = error?.Message ?? $"Lock request failed ({(int)response.StatusCode}).";
                MessageBox.Show(this, MessageText.Text, "FocusLock request failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var status = await response.Content.ReadFromJsonAsync<LockStatus>(lifetime.Token)
                ?? throw new JsonException("Empty lock response.");
            ShowStatus(status);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested)
            {
                ShowDisconnected();
                MessageText.Text = "Lock response unavailable. The request may have been saved; reconnecting to verify.";
            }
        }
        finally
        {
            submitting = false;
            LockButton.IsEnabled = connected && canLock && !lifetime.IsCancellationRequested;
        }
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        await lifetime.CancelAsync();
        if (pollingTask is not null)
        {
            try { await pollingTask; }
            catch (OperationCanceledException) { }
        }
        client?.Dispose();
    }
}
