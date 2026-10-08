using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace JenkinsStatus;

// A buildable Jenkins job. Result is the Jenkins result of the last completed build (SUCCESS, FAILURE, UNSTABLE, ABORTED, ...).
// LastBuildNumber is the most recent build (possibly still running), needed to cancel it or fetch its console output.
public record Project(string Name, bool IsBuilding, string Result, string BuildNumber, string WebUrl, string LastBuildNumber = "")
{
    public bool IsFailed => Result is "FAILURE" or "UNSTABLE";
    public bool IsSuccess => Result == "SUCCESS";
    public Color Color => IsBuilding ? Color.Orange : IsFailed ? Color.Red : IsSuccess ? Color.LimeGreen : Color.Gray;
}

public static class Jenkins
{
    private const string Fields = "fullName,url,color,lastBuild[number],lastCompletedBuild[number,result]";
    // 3 nesting levels (folder > multibranch > branch); add a level if deeper folders show up.
    private const string Tree = $"jobs[{Fields},jobs[{Fields},jobs[{Fields}]]]";

    public static List<Project> Parse(string json)
    {
        return Leaves(JsonNode.Parse(json)!["jobs"]).ToList();
    }

    // Folders/multibranch projects have "jobs" (or, at the deepest level, no "color"); only real jobs are returned.
    private static IEnumerable<Project> Leaves(JsonNode? jobs)
    {
        return jobs?.AsArray().SelectMany(j => j!["jobs"] is JsonArray children ? Leaves(children)
            : j["color"] is null ? [] : [ToProject(j)]) ?? [];
    }

    private static Project ToProject(JsonNode j)
    {
        JsonNode? last = j["lastCompletedBuild"];
        return new Project(
            Uri.UnescapeDataString((string)j["fullName"]!), // branch names are stored escaped, e.g. feature%2Ffoo
            ((string?)j["color"] ?? "").EndsWith("_anime"),
            (string?)last?["result"] ?? "",
            last?["number"]?.ToString() ?? "",
            (string?)j["url"] ?? "",
            j["lastBuild"]?["number"]?.ToString() ?? "");
    }

    public static async Task<List<Project>> FetchAsync(HttpClient http, Settings s)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, s.ServerUrl.TrimEnd('/') + "/api/json?tree=" + Tree);
        AddAuth(req, s);
        using HttpResponseMessage res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return Parse(await res.Content.ReadAsStringAsync());
    }

    public static Task StartBuildAsync(HttpClient http, Settings s, Project p)
    {
        return PostAsync(http, s, p.WebUrl + "build", p.WebUrl + "buildWithParameters");
    }

    public static Task CancelBuildAsync(HttpClient http, Settings s, Project p)
    {
        return PostAsync(http, s, p.WebUrl + p.LastBuildNumber + "/stop");
    }

    public static async Task<string> ConsoleTextAsync(HttpClient http, Settings s, Project p)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, p.WebUrl + p.LastBuildNumber + "/consoleText");
        AddAuth(req, s);
        using HttpResponseMessage res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync();
    }

    private static async Task PostAsync(HttpClient http, Settings s, string url, string? retryOn400 = null)
    {
        var res = await SendPost(http, s, url);
        if (res.StatusCode == HttpStatusCode.BadRequest && retryOn400 != null)
        {
            // Jenkins refuses POST .../build with 400 for jobs that have build parameters and tells
            // the caller to use .../buildWithParameters (an empty POST starts with the default values).
            // Other status codes are genuine errors and are not retried.
            res.Dispose();
            res = await SendPost(http, s, retryOn400);
        }
        res.EnsureSuccessStatusCode();
        res.Dispose();
    }

    private static async Task<HttpResponseMessage> SendPost(HttpClient http, Settings s, string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        AddAuth(req, s);
        await AddCrumbAsync(req, http, s);
        return await http.SendAsync(req);
    }

    private static void AddAuth(HttpRequestMessage req, Settings s)
    {
        if (s.User != "")
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.User}:{s.ApiToken}")));
        }
    }

    // Jenkins rejects POSTs without a CSRF crumb when CSRF protection is enabled; harmless to skip if it's off.
    private static async Task AddCrumbAsync(HttpRequestMessage req, HttpClient http, Settings s)
    {
        try
        {
            using var creq = new HttpRequestMessage(HttpMethod.Get, s.ServerUrl.TrimEnd('/') + "/crumbIssuer/api/json");
            AddAuth(creq, s);
            using HttpResponseMessage cres = await http.SendAsync(creq);
            if (!cres.IsSuccessStatusCode)
            {
                return;
            }

            JsonNode json = JsonNode.Parse(await cres.Content.ReadAsStringAsync())!;
            req.Headers.Add((string)json["crumbRequestField"]!, (string)json["crumb"]!);
        }
        catch { /* no crumb issuer (CSRF protection disabled); proceed without one */ }
    }

    // Projects whose last completed build changed since the previous poll.
    public static List<Project> Finished(IReadOnlyDictionary<string, Project> before, IEnumerable<Project> after)
    {
        return after.Where(p => before.TryGetValue(p.Name, out Project? old) && old.BuildNumber != p.BuildNumber).ToList();
    }

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
        get => ProtectedToken == "" ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), null, DataProtectionScope.CurrentUser));
        set => ProtectedToken = value == "" ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JenkinsStatus", "settings.json");

    public static Settings Load()
    {
        return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
