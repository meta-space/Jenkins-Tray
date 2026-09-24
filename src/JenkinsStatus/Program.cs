namespace JenkinsStatus;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "JenkinsStatus.SingleInstance", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
