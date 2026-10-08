using System.Drawing;
using System.Net;

namespace JenkinsStatus.Tests;

public class JenkinsTests
{
    // Shape of /api/json?tree=jobs[fullName,url,color,lastBuild[number],lastCompletedBuild[number,result],jobs[...]]
    private const string Json = """
        {"jobs":[
          {"fullName":"app","url":"http://j/job/app/","color":"blue","lastBuild":{"number":12},"lastCompletedBuild":{"number":12,"result":"SUCCESS"}},
          {"fullName":"new","url":"http://j/job/new/","color":"notbuilt","lastCompletedBuild":null},
          {"fullName":"mb","url":"http://j/job/mb/","jobs":[
            {"fullName":"mb/feature%2Fx","url":"http://j/job/mb/job/feature%252Fx/","color":"red_anime","lastBuild":{"number":8},"lastCompletedBuild":{"number":7,"result":"FAILURE"}}
          ]},
          {"fullName":"deep-folder","url":"http://j/job/deep-folder/"}
        ]}
        """;

    [Fact]
    public void ParsesJobsFlatteningFolders()
    {
        List<Project> ps = Jenkins.Parse(Json);
        Assert.Equal(["app", "new", "mb/feature/x"], ps.Select(p => p.Name));
        Assert.Equal(new Project("app", false, "SUCCESS", "12", "http://j/job/app/", "12"), ps[0]);
        Assert.Equal(new Project("new", false, "", "", "http://j/job/new/"), ps[1]);
        Assert.True(ps[2].IsBuilding && ps[2].IsFailed);
        Assert.Equal("8", ps[2].LastBuildNumber); // the running build, distinct from the last *completed* one (#7)
    }

    [Fact]
    public void FinishedOnlyReportsChangedBuildsOfKnownProjects()
    {
        var before = Jenkins.Parse(Json).ToDictionary(p => p.Name);
        List<Project> after = Jenkins.Parse(Json.Replace("\"number\":7", "\"number\":8").Replace("\"app\"", "\"other\""));
        Assert.Equal(["mb/feature/x"], Jenkins.Finished(before, after).Select(p => p.Name));
        Assert.Empty(Jenkins.Finished(new Dictionary<string, Project>(), after)); // first poll never notifies
    }

    [Fact]
    public void OverallPrefersFailureThenBuilding()
    {
        List<Project> ps = Jenkins.Parse(Json);
        Assert.Equal(Color.Red, Jenkins.Overall(ps));
        Assert.Equal(Color.LimeGreen, Jenkins.Overall(ps.Take(2)));
        Assert.Equal(Color.Gray, Jenkins.Overall([]));
    }

    private static Project SampleProject = new("p", false, "", "", "http://j/job/p/", "");

    // Fake Jenkins that answers each request with a scripted status; records the requests in order.
    private sealed class ScriptedServer(Func<string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add($"{request.Method} {url}");
            return Task.FromResult(responder(url));
        }

        public IEnumerable<string> Posts => Requests.Where(r => r.StartsWith("POST "));
    }

    [Fact]
    public async Task StartBuildFallsBackToBuildWithParametersWhenBuildIsRejected()
    {
        var server = new ScriptedServer(url => url.EndsWith("build") ? new(HttpStatusCode.BadRequest) : new(HttpStatusCode.OK));
        await Jenkins.StartBuildAsync(new HttpClient(server), new Settings(), SampleProject);
        Assert.Equal(["POST http://j/job/p/build", "POST http://j/job/p/buildWithParameters"], server.Posts);
    }

    [Fact]
    public async Task StartBuildStaysOnBuildWhenItIsAccepted()
    {
        var server = new ScriptedServer(_ => new(HttpStatusCode.OK));
        await Jenkins.StartBuildAsync(new HttpClient(server), new Settings(), SampleProject);
        Assert.Equal(["POST http://j/job/p/build"], server.Posts);
    }

    [Fact]
    public async Task StartBuildDoesNotRetryAnythingOtherThan400()
    {
        var server = new ScriptedServer(_ => new(HttpStatusCode.Forbidden));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            Jenkins.StartBuildAsync(new HttpClient(server), new Settings(), SampleProject));
        Assert.Equal(["POST http://j/job/p/build"], server.Posts);
    }
}
