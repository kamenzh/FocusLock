using System.Windows;

namespace LockController;

public partial class UnlockConfirmationWindow : Window
{
    public UnlockConfirmationWindow(string target)
    {
        InitializeComponent();
        PromptText.Text = $"Unlock {target} now?\n\nThis will end the active FocusLock restriction early.";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
