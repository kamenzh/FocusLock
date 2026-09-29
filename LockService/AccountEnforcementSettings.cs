namespace LockService;

public sealed class AccountEnforcementSettings
{
    public bool Enabled { get; set; }
    // Deliberately empty: enabling enforcement also requires choosing a target.
    public string TargetUsername { get; set; } = "";
    public string RecoveryAdminUsername { get; set; } = "";
    public bool DryRun { get; set; } = true;
}
