namespace MicuQuotaWidget.Services;

/// <summary>
/// Isolates current-user secret storage so view models never know the platform-specific credential mechanism.
/// </summary>
public interface IApiKeyStore
{
    /// <summary>
    /// Returns the locally stored API key, or <see langword="null"/> when the user has not configured one.
    /// </summary>
    string? ReadApiKey();

    /// <summary>
    /// Saves a non-empty API key for the current Windows user.
    /// </summary>
    void SaveApiKey(string apiKey);

    /// <summary>
    /// Removes the current user's saved API key during an explicit reset action.
    /// </summary>
    void DeleteApiKey();
}
