using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FocusLock.Client;
using FocusLock.Security;
using Shared;

namespace LockController;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private HttpClient? http;
    private FocusLockApiClient? client;
    private Task? pollingTask;
    private bool submitting;
    private bool connected;
    private int requestVersion;
    private bool canLock;
    private bool canUnlock;

    public MainWindow() => InitializeComponent();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = await ControllerSettings.LoadAsync(lifetime.Token);
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                { BaseAddress = settings.GetBaseAddress(), Timeout = TimeSpan.FromSeconds(5) };
            client = new FocusLockApiClient(http, new DpapiSecretStore(settings.SecretFile));
            pollingTask = PollAsync(lifetime.Token);
            await pollingTask;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { MessageText.Text = "Controller configuration error: " + exception.Message; }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            if (!submitting) await RefreshAsync(ct);
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var version = requestVersion;
        try
        {
            var status = await client!.GetStatusAsync(ct);
            if (!submitting && version == requestVersion) ShowStatus(status);
        }
        catch (FocusLockApiException exception)
        { if (!submitting && version == requestVersion) ShowFailure(exception); }
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
        canUnlock = status.Locked && !status.RecoveryRequired;
        UpdateButtons();
    }

    private void ShowFailure(FocusLockApiException exception)
    {
        connected = false;
        canLock = canUnlock = false;
        ConnectionText.Text = exception.Kind switch
        {
            ApiFailureKind.Authentication => "Authentication error",
            ApiFailureKind.Unreachable => "Disconnected",
            _ => "API error"
        };
        StateText.Text = "UNKNOWN";
        CountdownPanel.Visibility = Visibility.Collapsed;
        MessageText.Text = exception.Message;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        LockButton.IsEnabled = connected && canLock && !submitting && !lifetime.IsCancellationRequested;
        UnlockButton.IsEnabled = connected && canUnlock && !submitting && !lifetime.IsCancellationRequested;
    }

    private void Preset_Click(object sender, RoutedEventArgs e) => MinutesText.Text = (string)((Button)sender).Tag;

    private async void Lock_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MinutesText.Text, out var minutes) || minutes is < 1 or > 720)
        {
            MessageBox.Show(this, "Enter a whole number from 1 through 720 minutes.", "Invalid duration", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await ExecuteCommandAsync(async current =>
        {
            if (!canLock || MessageBox.Show(this, LockConfirmation.Create(current, minutes), "FocusLock", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return null;
            return await client!.LockAsync(minutes, lifetime.Token);
        });
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync(async current =>
        {
            if (!canUnlock) return null;
            var dialog = new UnlockConfirmationWindow(current.TargetUsername ?? "the active FocusLock timer") { Owner = this };
            if (dialog.ShowDialog() != true) return null;
            return await client!.UnlockAsync(lifetime.Token);
        });
    }

    private async Task ExecuteCommandAsync(Func<LockStatus, Task<LockStatus?>> command)
    {
        if (submitting || client is null) return;
        submitting = true;
        requestVersion++;
        UpdateButtons();
        try
        {
            var current = await client.GetStatusAsync(lifetime.Token);
            ShowStatus(current);
            var result = await command(current);
            if (result is not null) ShowStatus(result);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (FocusLockApiException exception)
        {
            ShowFailure(exception);
            if (exception.Kind == ApiFailureKind.Unreachable)
                MessageText.Text += " A submitted command may have completed; polling will verify the current state.";
            if (!lifetime.IsCancellationRequested)
                MessageBox.Show(this, MessageText.Text, "FocusLock request failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { submitting = false; UpdateButtons(); }
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        await lifetime.CancelAsync();
        if (pollingTask is not null)
        {
            try { await pollingTask; }
            catch (OperationCanceledException) { }
        }
        http?.Dispose();
    }
}
