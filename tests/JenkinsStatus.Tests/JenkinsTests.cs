using System.Drawing;

namespace JenkinsStatus.Tests;

public class JenkinsTests
{
    const string Xml = """
        <Projects>
          <Project name="app" activity="Sleeping" lastBuildStatus="Success" lastBuildLabel="12" webUrl="http://j/job/app/" />
          <Project name="folder » lib" activity="Building" lastBuildStatus="Failure" lastBuildLabel="7" webUrl="http://j/job/folder/job/lib/" />
        </Projects>
        """;

    [Fact]
    public void ParsesCcXml()
    {
        var ps = Jenkins.Parse(Xml);
        Assert.Equal(2, ps.Count);
        Assert.Equal(new Project("app", "Sleeping", "Success", "12", "http://j/job/app/"), ps[0]);
        Assert.True(ps[1].IsBuilding && ps[1].IsFailed);
    }

    [Fact]
    public void FinishedOnlyReportsChangedLabelsOfKnownProjects()
    {
        var before = Jenkins.Parse(Xml).ToDictionary(p => p.Name);
        var after = Jenkins.Parse(Xml.Replace("\"7\"", "\"8\"").Replace("name=\"app\"", "name=\"new\""));
        Assert.Equal(["folder » lib"], Jenkins.Finished(before, after).Select(p => p.Name));
        Assert.Empty(Jenkins.Finished(new Dictionary<string, Project>(), after)); // first poll never notifies
    }

    [Fact]
    public void OverallPrefersFailureThenBuilding()
    {
        var ps = Jenkins.Parse(Xml);
        Assert.Equal(Color.Red, Jenkins.Overall(ps));
        Assert.Equal(Color.LimeGreen, Jenkins.Overall(ps.Take(1)));
        Assert.Equal(Color.Gray, Jenkins.Overall([]));
    }
}
