using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace JenkinsStatus;

public class MainForm : Form
{
    static readonly Color[] StatusColors = [Color.LimeGreen, Color.Red, Color.Orange, Color.Gray];

    readonly Settings settings = Settings.Load();
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly System.Windows.Forms.Timer timer = new();
    readonly NotifyIcon tray = new() { Text = "Jenkins Status", Visible = true };
    readonly Dictionary<Color, Icon> icons = [];
    readonly ListView monitored = NewList(checkBoxes: false);
    readonly ListView all = NewList(checkBoxes: true);
    readonly TextBox server = new() { Width = 250 };
    readonly TextBox user = new() { Width = 120 };
    readonly TextBox token = new() { Width = 200, UseSystemPasswordChar = true };
    readonly NumericUpDown interval = new() { Minimum = 5, Maximum = 3600, Width = 70 };
    readonly ToolStripStatusLabel status = new();
    Dictionary<string, Project> last = [];
    bool loading, polling, started;
    Project? menuProject;

    public MainForm()
    {
        Text = "Jenkins Status";
        Size = new Size(900, 550);
        tray.Icon = Icon = IconFor(Color.Gray);

        all.ItemChecked += (_, e) =>
        {
            if (loading) return;
            if (e.Item.Checked) settings.Projects.Add(e.Item.Name); else settings.Projects.Remove(e.Item.Name);
            settings.Save();
            UpdateList();
            UpdateTray();
        };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add("Monitored");
        tabs.TabPages.Add("All projects (check to monitor)");
        tabs.TabPages[0].Controls.Add(monitored);
        tabs.TabPages[1].Controls.Add(all);
        if (settings.Projects.Count == 0) tabs.SelectedIndex = 1;

        server.Text = settings.ServerUrl;
        user.Text = settings.User;
        token.Text = settings.ApiToken;
        interval.Value = Math.Clamp(settings.PollSeconds, 5, 3600);
        var apply = new Button { Text = "Apply", AutoSize = true };
        apply.Click += async (_, _) => await ApplyAsync();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        top.Controls.AddRange([L("Server"), server, L("User"), user, L("API token"), token, L("Poll (s)"), interval, apply]);
        var strip = new StatusStrip();
        strip.Items.Add(status);
        Controls.AddRange([tabs, top, strip]); // Fill must be added first to dock last
        // Create the checkbox list's handle while it is empty: a ListView whose handle is created later (tab first shown)
        // re-syncs its check states and fires ItemChecked for every row, which would re-enter UpdateList and duplicate rows.
        _ = all.Handle;

        var listMenu = new ContextMenuStrip();
        var startItem = new ToolStripMenuItem("Start build");
        var cancelItem = new ToolStripMenuItem("Cancel build");
        var consoleItem = new ToolStripMenuItem("Copy console output");
        listMenu.Items.AddRange([startItem, cancelItem, consoleItem]);
        listMenu.Opening += (_, e) =>
        {
            menuProject = (listMenu.SourceControl as ListView)?.FocusedItem?.Tag as Project;
            e.Cancel = menuProject is null;
            if (menuProject is not { } p) return;
            startItem.Enabled = !p.IsBuilding;
            cancelItem.Enabled = p.IsBuilding;
            consoleItem.Enabled = p.LastBuildNumber != "";
        };
        startItem.Click += async (_, _) => await RunMenuAction(p => Jenkins.StartBuildAsync(http, settings, p), "Build started");
        cancelItem.Click += async (_, _) => await RunMenuAction(p => Jenkins.CancelBuildAsync(http, settings, p), "Build cancelled");
        consoleItem.Click += async (_, _) =>
        {
            try
            {
                var text = await Jenkins.ConsoleTextAsync(http, settings, menuProject!);
                Clipboard.SetText(text == "" ? " " : text);
                status.Text = "Console output copied to clipboard";
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
        };
        foreach (var list in new[] { monitored, all })
        {
            list.ContextMenuStrip = listMenu;
            list.MouseUp += (_, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                if (list.HitTest(e.Location).Item is { } item) { list.SelectedItems.Clear(); item.Selected = item.Focused = true; }
            };
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowWindow());
        menu.Items.Add("Refresh now", null, async (_, _) => await PollAsync());
        menu.Items.Add("Open Jenkins", null, (_, _) => Open(settings.ServerUrl));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowWindow();
        tray.BalloonTipClicked += (_, _) => ShowWindow();

        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        FormClosed += (_, _) => tray.Dispose();

        timer.Interval = settings.PollSeconds * 1000;
        timer.Tick += async (_, _) => await PollAsync();
        timer.Start();
        _ = PollAsync();
    }

    // Start hidden in the tray; only show the window on first run so credentials can be entered.
    protected override void SetVisibleCore(bool value)
    {
        if (!started) { started = true; if (!IsHandleCreated) CreateHandle(); value = settings.User == ""; }
        base.SetVisibleCore(value);
    }

    async Task ApplyAsync()
    {
        if (!Uri.TryCreate(server.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show(this, "Server must be an http(s) URL.", Text);
            return;
        }
        settings.ServerUrl = uri.ToString();
        settings.User = user.Text.Trim();
        settings.ApiToken = token.Text.Trim();
        settings.PollSeconds = (int)interval.Value;
        settings.Save();
        timer.Interval = settings.PollSeconds * 1000;
        last = []; // server may have changed; don't report its builds as "finished"
        UpdateList();
        await PollAsync();
    }

    async Task PollAsync()
    {
        if (polling) return;
        polling = true;
        try
        {
            var projects = await Jenkins.FetchAsync(http, settings);
            var done = Jenkins.Finished(last, projects).Where(p => settings.Projects.Contains(p.Name)).ToList();
            if (done.Count > 0)
                tray.ShowBalloonTip(5000,
                    done.Any(p => p.IsFailed) ? "Build failed" : "Build succeeded",
                    string.Join("\n", done.Select(p => $"{p.Name} #{p.BuildNumber}: {p.Result}")),
                    done.Any(p => p.IsFailed) ? ToolTipIcon.Error : ToolTipIcon.Info);

            last = projects.ToDictionary(p => p.Name);
            UpdateList();
            UpdateTray();
            status.Text = $"Updated {DateTime.Now:T} - {projects.Count} projects, {settings.Projects.Count} monitored";
        }
        catch (Exception ex) // a tray poller must survive network/auth/parse errors and keep retrying
        {
            status.Text = $"Error {DateTime.Now:T}: {ex.Message}";
            tray.Icon = Icon = IconFor(Color.Gray);
            tray.Text = Truncate("Jenkins Status: " + ex.Message);
        }
        finally { polling = false; }
    }

    async Task RunMenuAction(Func<Project, Task> action, string doneMessage)
    {
        try { await action(menuProject!); status.Text = doneMessage; await PollAsync(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
    }

    void UpdateList()
    {
        loading = true;
        Sync(all, last.Values);
        Sync(monitored, last.Values.Where(p => settings.Projects.Contains(p.Name)));
        loading = false;
    }

    // Update rows in place (keeps selection and scroll position), adding/removing as projects come and go.
    void Sync(ListView list, IEnumerable<Project> projects)
    {
        var wanted = projects.ToDictionary(p => p.Name);
        list.BeginUpdate();
        foreach (var gone in list.Items.Cast<ListViewItem>().Where(i => !wanted.ContainsKey(i.Name)).ToList())
            list.Items.Remove(gone);
        foreach (var p in wanted.Values)
        {
            var item = list.Items[p.Name] ?? list.Items.Add(
                new ListViewItem([p.Name, "", "", ""]) { Name = p.Name, Checked = settings.Projects.Contains(p.Name) });
            item.SubItems[1].Text = p.Result;
            item.SubItems[2].Text = p.BuildNumber;
            item.SubItems[3].Text = p.IsBuilding ? "Building" : "";
            item.ImageKey = p.Color.Name;
            item.Tag = p;
        }
        list.EndUpdate();
    }

    static ListView NewList(bool checkBoxes)
    {
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = checkBoxes, FullRowSelect = true, Sorting = SortOrder.Ascending };
        list.SmallImageList = new ImageList();
        foreach (var c in StatusColors) list.SmallImageList.Images.Add(c.Name, Dot(c));
        list.Columns.Add("Project", 450);
        list.Columns.Add("Last build", 100);
        list.Columns.Add("Build #", 80);
        list.Columns.Add("Activity", 100);
        list.ItemActivate += (_, _) => { if (list.FocusedItem?.Tag is Project p) Open(p.WebUrl); };
        return list;
    }

    void UpdateTray()
    {
        var watched = last.Values.Where(p => settings.Projects.Contains(p.Name)).ToList();
        tray.Icon = Icon = IconFor(Jenkins.Overall(watched));
        var failing = watched.Where(p => p.IsFailed).Select(p => p.Name).ToList();
        tray.Text = Truncate(failing.Count == 0
            ? $"Jenkins Status: {watched.Count} monitored, all OK"
            : $"Failing: {string.Join(", ", failing)}");
    }

    void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    static void Open(string url)
    {
        if (url != "") Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    static string Truncate(string s) => s.Length <= 127 ? s : s[..124] + "...";

    static Label L(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(8, 7, 0, 0) };

    Icon IconFor(Color c)
    {
        if (!icons.TryGetValue(c, out var icon)) icons[c] = icon = Icon.FromHandle(Dot(c).GetHicon());
        return icon;
    }

    static Bitmap Dot(Color c)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        using var brush = new SolidBrush(c);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillEllipse(brush, 1, 1, 13, 13);
        g.DrawEllipse(Pens.DimGray, 1, 1, 13, 13);
        return bmp;
    }
}
