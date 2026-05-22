using Argus.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/", () => Results.Content("""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Argus Recon Platform</title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #101214;
      --panel: #171b1f;
      --panel-2: #20262b;
      --line: #364049;
      --text: #edf2f4;
      --muted: #aab5bd;
      --blue: #4fb3ff;
      --green: #54d990;
      --amber: #f2b84b;
      --red: #ff6b6b;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      background: var(--bg);
      color: var(--text);
      font: 13px/1.4 ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }
    header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      padding: 14px 18px;
      border-bottom: 1px solid var(--line);
      background: #14181c;
    }
    h1 {
      margin: 0;
      font-size: 18px;
      font-weight: 650;
      letter-spacing: 0;
    }
    main {
      display: grid;
      grid-template-columns: 220px minmax(0, 1fr) 320px;
      min-height: calc(100vh - 54px);
    }
    nav, aside {
      background: var(--panel);
      border-right: 1px solid var(--line);
      padding: 12px;
    }
    aside {
      border-right: 0;
      border-left: 1px solid var(--line);
    }
    button, .chip {
      min-height: 28px;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: var(--panel-2);
      color: var(--text);
      padding: 4px 8px;
      font: inherit;
    }
    nav button {
      width: 100%;
      display: block;
      text-align: left;
      margin-bottom: 6px;
    }
    .toolbar {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      padding: 12px;
      border-bottom: 1px solid var(--line);
      background: #15191d;
    }
    .grid {
      overflow: auto;
      max-height: calc(100vh - 108px);
    }
    table {
      width: 100%;
      min-width: 1060px;
      border-collapse: collapse;
    }
    th, td {
      height: 32px;
      border-bottom: 1px solid #293139;
      padding: 5px 8px;
      text-align: left;
      white-space: nowrap;
    }
    th {
      position: sticky;
      top: 0;
      z-index: 1;
      background: #20262b;
      color: var(--muted);
      font-weight: 600;
    }
    .metric-row {
      display: grid;
      grid-template-columns: 1fr auto;
      gap: 8px;
      padding: 8px 0;
      border-bottom: 1px solid #293139;
    }
    .metric {
      font-size: 18px;
      font-weight: 700;
    }
    .ok { color: var(--green); }
    .warn { color: var(--amber); }
    .hot { color: var(--red); }
    .link { color: var(--blue); }
    @media (max-width: 980px) {
      main { grid-template-columns: 1fr; }
      nav, aside { border: 0; border-bottom: 1px solid var(--line); }
      aside { border-top: 1px solid var(--line); }
    }
  </style>
</head>
<body>
  <header>
    <h1>Argus Recon Platform</h1>
    <div class="chip">local Aspire control surface</div>
  </header>
  <main>
    <nav>
      <button>Command Center</button>
      <button>Programs</button>
      <button>Scope Explorer</button>
      <button>Asset Explorer</button>
      <button>Task Monitor</button>
      <button>Worker Fleet</button>
      <button>Live Events</button>
      <button>Rate Limits</button>
    </nav>
    <section>
      <div class="toolbar">
        <button>Refresh</button>
        <button>New Program</button>
        <button>Enqueue Task</button>
        <button>Saved Views</button>
        <span class="chip">server-side filters pending</span>
        <span class="chip">batch refresh: 5s</span>
      </div>
      <div class="grid">
        <table>
          <thead>
            <tr>
              <th>Type</th><th>Subtype</th><th>Value</th><th>Program</th><th>Scope</th>
              <th>Status</th><th>Interesting</th><th>Risk</th><th>First Seen</th><th>Last Seen</th>
              <th>Discovered By</th><th>HTTP</th><th>Content Type</th><th>Size</th><th>Tech</th><th>Tags</th><th>Relations</th>
            </tr>
          </thead>
          <tbody>
            <tr><td>Domain</td><td></td><td class="link">example.com</td><td>demo</td><td>wildcard</td><td class="ok">InScope</td><td>5</td><td>0</td><td>--</td><td>--</td><td>seed</td><td></td><td></td><td></td><td></td><td>seed</td><td>0</td></tr>
            <tr><td>Subdomain</td><td></td><td class="link">api.example.com</td><td>demo</td><td>wildcard</td><td class="ok">New</td><td>5</td><td>0</td><td>--</td><td>--</td><td>subfinder</td><td></td><td></td><td></td><td></td><td></td><td>1</td></tr>
            <tr><td>Url</td><td>LoginPage</td><td class="link">https://api.example.com/login</td><td>demo</td><td>wildcard</td><td class="warn">New</td><td>25</td><td>0</td><td>--</td><td>--</td><td>http-probe</td><td>200</td><td>text/html</td><td>18 KB</td><td>nginx</td><td>auth</td><td>3</td></tr>
          </tbody>
        </table>
      </div>
    </section>
    <aside>
      <div class="metric-row"><span>Assets discovered</span><span class="metric ok">0/s</span></div>
      <div class="metric-row"><span>Tasks running</span><span class="metric">0</span></div>
      <div class="metric-row"><span>Queue depth</span><span class="metric">0</span></div>
      <div class="metric-row"><span>Rate-limit waits</span><span class="metric warn">0</span></div>
      <div class="metric-row"><span>Worker failures</span><span class="metric hot">0</span></div>
    </aside>
  </main>
</body>
</html>
""", "text/html"));

app.Run();
