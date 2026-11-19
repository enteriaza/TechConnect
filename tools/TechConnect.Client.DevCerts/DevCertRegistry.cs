using System.Text.Json;
using System.Text.Json.Serialization;

namespace TechConnect.Client.DevCerts;

internal sealed class DevCertRegistry
{
    public string CaSubject { get; set; } = DevCertIdentities.CaSubject;

    public string ServerSubject { get; set; } = DevCertIdentities.ServerSubject;

    public string ClientSubject { get; set; } = DevCertIdentities.ClientSubject;

    public string CaThumbprint { get; set; } = string.Empty;

    public string ServerThumbprint { get; set; } = string.Empty;

    public string ClientThumbprint { get; set; } = string.Empty;

    public DateTimeOffset UpdatedUtc { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static DevCertRegistry? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<DevCertRegistry>(json, JsonOptions);
    }

    public void Save(string path)
    {
        UpdatedUtc = DateTimeOffset.UtcNow;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
