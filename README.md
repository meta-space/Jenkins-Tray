# JenkinsStatus

A CCTray-style tray monitor for Jenkins (default server `http://htkasrv084:8080/`).

- Run `dotnet run --project src/JenkinsStatus`. The window opens on first run.
- Enter your Jenkins user and API token (Jenkins → your name → Security → API Token), then click **Apply**.
- Check the projects you want to monitor. Closing or minimizing the window hides it to the tray. Double-click the tray icon to reopen it.
- Tray icon colors: red means a monitored build failed, orange means building, green means all builds succeeded, gray means unknown or an error.
- Settings are stored in `%APPDATA%\JenkinsStatus\settings.json`. The token is encrypted with DPAPI for the current user.
