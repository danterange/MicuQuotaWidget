using System.Text.Json;
using System.Text.Json.Serialization;
using MicuQuotaWidget.Core.Models;

namespace MicuQuotaWidget.Services;

/// <summary>
/// Stores the endpoint mode under LocalApplicationData while intentionally excluding credentials and remote response data.
/// </summary>
public sealed class JsonWidgetSettingsStore : IWidgetSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    private readonly string _settingsPath;

    /// <summary>
    /// Creates a store at the standard per-user location or at an injected path for deterministic UI tests.
    /// </summary>
    public JsonWidgetSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath
            ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MicuQuotaWidget",
                "settings.json");
    }

    /// <summary>
    /// Loads a valid endpoint mode and rejects damaged settings rather than silently switching a user's account contract.
    /// </summary>
    public async Task<WidgetSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!System.IO.File.Exists(_settingsPath))
        {
            return new WidgetSettings();
        }

        try
        {
            string json = await System.IO.File.ReadAllTextAsync(_settingsPath, cancellationToken);
            WidgetSettings? settings = JsonSerializer.Deserialize<WidgetSettings>(json, SerializerOptions);

            if (settings is null || !Enum.IsDefined(settings.EndpointKind))
            {
                throw new InvalidOperationException("本地设置文件包含不支持的 Micu 查询模式。");
            }

            return settings;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("本地设置文件无法读取，请在关闭程序后删除该设置文件并重新配置。", exception);
        }
    }

    /// <summary>
    /// Writes non-secret settings through a temporary file so an interrupted save cannot leave partial JSON behind.
    /// </summary>
    public async Task SaveAsync(WidgetSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!Enum.IsDefined(settings.EndpointKind))
        {
            throw new InvalidOperationException("不能保存不支持的 Micu 查询模式。");
        }

        string? directoryPath = System.IO.Path.GetDirectoryName(_settingsPath);

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new InvalidOperationException("本地设置路径无效。");
        }

        System.IO.Directory.CreateDirectory(directoryPath);
        string temporaryPath = $"{_settingsPath}.tmp";
        string json = JsonSerializer.Serialize(settings, SerializerOptions);

        await System.IO.File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        System.IO.File.Move(temporaryPath, _settingsPath, true);
    }
}
