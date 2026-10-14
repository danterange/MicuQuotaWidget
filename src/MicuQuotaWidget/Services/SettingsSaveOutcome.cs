namespace MicuQuotaWidget.Services;

/// <summary>
/// Describes the observable outcome of saving non-secret endpoint settings and a Windows Credential Manager API key as one coordinated UI operation.
/// </summary>
public enum SettingsSaveOutcome
{
    /// <summary>
    /// The endpoint mode and required credential are both available, so the widget may issue a fresh query.
    /// </summary>
    Saved,

    /// <summary>
    /// No existing key was present and the user did not supply one, so no endpoint change was attempted.
    /// </summary>
    MissingApiKey,

    /// <summary>
    /// The endpoint file could not be written, so the prior configuration remains authoritative.
    /// </summary>
    EndpointSaveFailed,

    /// <summary>
    /// Credential persistence failed after the endpoint mode was saved, but the prior readable endpoint was restored.
    /// </summary>
    CredentialSaveFailedRolledBack,

    /// <summary>
    /// Credential persistence failed after replacing a damaged settings file, leaving the recovered endpoint in place for a retry.
    /// </summary>
    CredentialSaveFailedAfterRecovery,

    /// <summary>
    /// Credential persistence failed and restoration of the prior endpoint also failed, so the user must confirm the visible setting.
    /// </summary>
    CredentialSaveFailedRollbackFailed
}
