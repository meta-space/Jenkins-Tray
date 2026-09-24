using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace JenkinsStatus;

// One <Project> entry of Jenkins' CCTray feed (cc.xml).
public record Project(string Name, string Activity, string LastBuildStatus, string LastBuildLabel, string WebUrl)
{
    public bool IsBuilding => Activity == "Building";
    public bool IsFailed => LastBuildStatus is "Failure" or "Exception";
    public bool IsSuccess => LastBuildStatus == "Success";
    public Color Color => IsBuilding ? Color.Orange : IsFailed ? Color.Red : IsSuccess ? Color.LimeGreen : Color.Gray;
}

public static class Jenkins
{
    public static List<Project> Parse(string ccXml) =>
        XDocument.Parse(ccXml).Root!.Elements("Project").Select(p => new Project(
            (string?)p.Attribute("name") ?? "",
            (string?)p.Attribute("activity") ?? "",
            (string?)p.Attribute("lastBuildStatus") ?? "",
            (string?)p.Attribute("lastBuildLabel") ?? "",
            (string?)p.Attribute("webUrl") ?? "")).ToList();

    public static async Task<List<Project>> FetchAsync(HttpClient http, Settings s)
    {
        // ?recursive includes jobs inside folders / multibranch pipelines.
        using var req = new HttpRequestMessage(HttpMethod.Get, s.ServerUrl.TrimEnd('/') + "/cc.xml?recursive");
        if (s.User != "")
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.User}:{s.ApiToken}")));
        using var res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return Parse(await res.Content.ReadAsStringAsync());
    }

    // Projects whose last completed build changed since the previous poll.
    public static List<Project> Finished(IReadOnlyDictionary<string, Project> before, IEnumerable<Project> after) =>
        after.Where(p => before.TryGetValue(p.Name, out var old) && old.LastBuildLabel != p.LastBuildLabel).ToList();

    public static Color Overall(IEnumerable<Project> monitored)
    {
        var ps = monitored.ToList();
        return ps.Any(p => p.IsFailed) ? Color.Red
             : ps.Any(p => p.IsBuilding) ? Color.Orange
             : ps.Any(p => p.IsSuccess) ? Color.LimeGreen
             : Color.Gray;
    }
}

public class Settings
{
    public string ServerUrl { get; set; } = "http://htkasrv084:8080/";
    public string User { get; set; } = "";
    public string ProtectedToken { get; set; } = ""; // DPAPI, current Windows user only
    public int PollSeconds { get; set; } = 30;
    public HashSet<string> Projects { get; set; } = [];

    [JsonIgnore]
    public string ApiToken
    {
        get => ProtectedToken == "" ? "" : Encoding.UTF8.GetString(
            ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), null, DataProtectionScope.CurrentUser));
        set => ProtectedToken = value == "" ? "" : Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JenkinsStatus", "settings.json");

    public static Settings Load() =>
        File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new();

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
