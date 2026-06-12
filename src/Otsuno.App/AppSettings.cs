using System.IO;
using System.Text.Json;

namespace Otsuno.App;

public record AppSettings(
    string TranslationModel,
    string TargetLanguage,
    double TranslationFrequency,
    bool DebugMode = false,
    string PipelinePreset = "LowLatency",
    string SourceLanguage = "Detect language",
    string OcrEngine = "PaddleOCR",
    double? WindowLeft = null,
    double? WindowTop = null,
    double? WindowWidth = null,
    double? WindowHeight = null,
    string WindowState = "Normal"
) {
    public static AppSettings Default { get; } = new("qwen2.5:1.5b", "ja", 3);
}

public class AppSettingsStore {
    protected readonly string settingsPath;

    public AppSettingsStore() : this(GetDefaultSettingsPath()) {
    }

    public AppSettingsStore(string settingsPath) {
        this.settingsPath = settingsPath;
    }

    public virtual AppSettings Load() {
        try {
            if (!File.Exists(settingsPath)) {
                return AppSettings.Default;
            }

            var json = File.ReadAllText(settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? AppSettings.Default;
        } catch {
            return AppSettings.Default;
        }
    }

    public virtual void Save(AppSettings settings) {
        try {
            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(directory)) {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(settingsPath, json);
        } catch {
        }
    }

    protected static string GetDefaultSettingsPath() {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Otsuno", "settings.json");
    }
}
