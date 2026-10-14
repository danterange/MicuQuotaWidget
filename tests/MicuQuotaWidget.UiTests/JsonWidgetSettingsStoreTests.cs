using MicuQuotaWidget.Core.Models;
using MicuQuotaWidget.Services;

namespace MicuQuotaWidget.UiTests;

/// <summary>
/// Verifies that a malformed non-secret settings file can be replaced through the same persistence layer used by the settings dialog.
/// </summary>
public sealed class JsonWidgetSettingsStoreTests
{
    /// <summary>
    /// Confirms that saving a valid endpoint mode after a malformed file restores normal loading without requiring manual deletion.
    /// </summary>
    [Fact]
    public async Task SaveAsyncAfterMalformedSettingsRestoresLoadability()
    {
        string temporaryDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MicuQuotaWidgetTests", Guid.NewGuid().ToString("N"));
        string settingsPath = System.IO.Path.Combine(temporaryDirectory, "settings.json");
        System.IO.Directory.CreateDirectory(temporaryDirectory);

        try
        {
            await System.IO.File.WriteAllTextAsync(settingsPath, "{not-json");
            var store = new JsonWidgetSettingsStore(settingsPath);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync());
            await store.SaveAsync(new WidgetSettings(MicuEndpointKind.MicuSubscription));
            WidgetSettings restored = await store.LoadAsync();

            Assert.Equal(MicuEndpointKind.MicuSubscription, restored.EndpointKind);
        }
        finally
        {
            if (System.IO.Directory.Exists(temporaryDirectory))
            {
                System.IO.Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }
}
