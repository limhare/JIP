using System.Text.Json;

namespace InstPlayerApp.Models;

public class AppSettings
{
    public float Volume { get; set; } = 1.0f;
    public int Pitch { get; set; } = 0;
    public float Tempo { get; set; } = 0;
    public List<string> Playlist { get; set; } = [];
    public List<string> LibraryFolders { get; set; } = [];
    public bool RepeatOne { get; set; }
    public bool RepeatAll { get; set; }
    public bool Shuffle { get; set; }
    public int LyricsFontSize { get; set; } = 14;
    public string Language { get; set; } = "ko";
    public int RecordSyncMs { get; set; } = 250;

    private static string FilePath => Path.Combine(FileSystem.AppDataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }
}
