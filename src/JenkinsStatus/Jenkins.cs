using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace JenkinsStatus;

// A buildable Jenkins job. Result is the Jenkins result of the last completed build (SUCCESS, FAILURE, UNSTABLE, ABORTED, ...).
public record Project(string Name, bool IsBuilding, string Result, string BuildNumber, string WebUrl)
{
    public bool IsFailed => Result is "FAILURE" or "UNSTABLE";
    public bool IsSuccess => Result == "SUCCESS";
    public Color Color => IsBuilding ? Color.Orange : IsFailed ? Color.Red : IsSuccess ? Color.LimeGreen : Color.Gray;
}

public static class Jenkins
{
    const string Fields = "fullName,url,color,lastCompletedBuild[number,result]";
    // 3 nesting levels (folder > multibranch > branch); add a level if deeper folders show up.
    const string Tree = $"jobs[{Fields},jobs[{Fields},jobs[{Fields}]]]";

    public static List<Project> Parse(string json) => Leaves(JsonNode.Parse(json)!["jobs"]).ToList();

    // Folders/multibranch projects have "jobs" (or, at the deepest level, no "color"); only real jobs are returned.
    static IEnumerable<Project> Leaves(JsonNode? jobs) =>
        jobs?.AsArray().SelectMany(j => j!["jobs"] is JsonArray children ? Leaves(children)
            : j["color"] is null ? [] : [ToProject(j)]) ?? [];

    static Project ToProject(JsonNode j)
    {
        var last = j["lastCompletedBuild"];
        return new Project(
            Uri.UnescapeDataString((string)j["fullName"]!), // branch names are stored escaped, e.g. feature%2Ffoo
            ((string?)j["color"] ?? "").EndsWith("_anime"),
            (string?)last?["result"] ?? "",
            last?["number"]?.ToString() ?? "",
            (string?)j["url"] ?? "");
    }

    public static async Task<List<Project>> FetchAsync(HttpClient http, Settings s)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, s.ServerUrl.TrimEnd('/') + "/api/json?tree=" + Tree);
        if (s.User != "")
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.User}:{s.ApiToken}")));
        using var res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return Parse(await res.Content.ReadAsStringAsync());
    }

    // Projects whose last completed build changed since the previous poll.
    public static List<Project> Finished(IReadOnlyDictionary<string, Project> before, IEnumerable<Project> after) =>
        after.Where(p => before.TryGetValue(p.Name, out var old) && old.BuildNumber != p.BuildNumber).ToList();

    public static Color Overall(IEnumerable<Project> monitored)
    {
        var ps = monitored.ToList();
        return 
              ps.Any(p => p.IsBuilding) ? Color.Orange
             : ps.Any(p => p.IsFailed) ? Color.Red
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
