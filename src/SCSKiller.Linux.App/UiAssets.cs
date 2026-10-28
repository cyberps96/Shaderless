namespace SCSKiller.Linux.App;

public static class UiAssets
{
    public static string GetHtml() => Html.Replace("{{VERSION}}", SCSKiller.Linux.Core.AppPaths.Version);

    const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Shaderless — shader stutter killer for Linux</title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #0d1117;
      --card-bg: #161b22;
      --card-border: #30363d;
      --border: #30363d;
      --text-main: #f0f6fc;
      --text-muted: #8b949e;
      --accent: #58a6ff;
      --accent-hover: #79c0ff;
      --green: #238636;
      --green-glow: #2ea043;
      --orange: #d29922;
      --red: #f85149;
      --purple: #bc8cff;
    }
    * { box-sizing: border-box; margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif; }
    body { background-color: var(--bg); color: var(--text-main); height: 100vh; display: flex; flex-direction: column; overflow: hidden; }
    
    /* Top Header */
    header {
      background-color: var(--card-bg);
      border-bottom: 1px solid var(--card-border);
      padding: 14px 24px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      flex-wrap: wrap;
      gap: 10px;
      user-select: none;
    }
    .brand {
      display: flex;
      align-items: center;
      gap: 12px;
    }
    .brand-icon {
      width: 32px;
      height: 32px;
      background: linear-gradient(135deg, #a371f7, #388bfd);
      border-radius: 8px;
      display: flex;
      align-items: center;
      justify-content: center;
      font-weight: 900;
      font-size: 18px;
      color: #fff;
      box-shadow: 0 0 12px rgba(163, 113, 247, 0.4);
    }
    .brand h1 { font-size: 19px; font-weight: 700; letter-spacing: 0.5px; }
    .brand-subtitle { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 1px; }

    /* Driver Status Pill */
    .driver-pill {
      background-color: #21262d;
      border: 1px solid var(--card-border);
      border-radius: 20px;
      padding: 6px 16px;
      display: flex;
      align-items: center;
      gap: 12px;
      font-size: 12px;
    }
    .status-dot { width: 8px; height: 8px; border-radius: 50%; background-color: #3fb950; box-shadow: 0 0 8px #3fb950; }
    .driver-meta { color: var(--text-muted); }
    .driver-meta strong { color: var(--text-main); }

    /* Action Buttons */
    .header-actions { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
    button {
      cursor: pointer;
      border-radius: 6px;
      padding: 8px 14px;
      font-size: 13px;
      font-weight: 600;
      border: 1px solid transparent;
      transition: all 0.15s ease;
      display: inline-flex;
      align-items: center;
      gap: 6px;
    }
    .btn-primary { background-color: var(--green); color: #fff; border-color: rgba(240, 246, 252, 0.1); }
    .btn-primary:hover { background-color: var(--green-glow); box-shadow: 0 0 10px rgba(46, 160, 67, 0.4); }
    .btn-secondary { background-color: #21262d; color: var(--text-main); border-color: var(--card-border); }
    .btn-secondary:hover { background-color: #30363d; }
    .btn-support { background-color: rgba(188, 140, 255, 0.12); color: #d2a8ff; border-color: rgba(188, 140, 255, 0.4); }
    .btn-support:hover { background-color: rgba(188, 140, 255, 0.24); border-color: #bc8cff; box-shadow: 0 0 10px rgba(188, 140, 255, 0.25); }
    .btn-danger { background-color: rgba(248, 81, 73, 0.15); color: #f85149; border-color: rgba(248, 81, 73, 0.4); }
    .btn-danger:hover { background-color: #f85149; color: #fff; }

    /* Main Container */
    main {
      flex: 1;
      padding: 24px;
      overflow-y: auto;
    }
    .section-title {
      font-size: 14px;
      font-weight: 600;
      color: var(--text-muted);
      text-transform: uppercase;
      letter-spacing: 1px;
      margin-bottom: 12px;
      display: flex;
      justify-content: space-between;
      align-items: center;
      flex-wrap: wrap;
      gap: 8px;
    }

    /* Launcher & Library Filter Bar */
    .filter-bar {
      display: flex;
      align-items: center;
      justify-content: space-between;
      flex-wrap: wrap;
      gap: 10px;
      margin-bottom: 18px;
      padding: 10px 12px;
      background-color: var(--card-bg);
      border: 1px solid var(--card-border);
      border-radius: 10px;
    }
    .filter-pills {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 6px;
    }
    .filter-divider {
      width: 1px;
      height: 22px;
      background-color: var(--card-border);
      margin: 0 4px;
    }
    .filter-pill {
      background-color: #0d1117;
      color: var(--text-muted);
      border: 1px solid var(--card-border);
      border-radius: 18px;
      padding: 5px 12px;
      font-size: 12px;
      font-weight: 600;
      cursor: pointer;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      transition: all 0.15s ease;
    }
    .filter-pill:hover {
      color: var(--text-main);
      border-color: #484f58;
      background-color: #21262d;
    }
    .filter-pill.active-all {
      background-color: rgba(88, 166, 255, 0.16);
      color: #79c0ff;
      border-color: rgba(88, 166, 255, 0.55);
    }
    .filter-pill.active-steam {
      background-color: #1b2838;
      color: #66c0f4;
      border-color: #66c0f4aa;
    }
    .filter-pill.active-heroic {
      background-color: #33152c;
      color: #f47bcf;
      border-color: #f47bcfaa;
    }
    .filter-pill.active-lutris {
      background-color: #38240f;
      color: #f5a623;
      border-color: #f5a623aa;
    }
    .filter-pill.active-needs {
      background-color: rgba(210, 153, 34, 0.18);
      color: #e3b341;
      border-color: rgba(210, 153, 34, 0.55);
    }
    .filter-count {
      font-size: 11px;
      padding: 1px 6px;
      border-radius: 10px;
      background-color: rgba(255, 255, 255, 0.08);
      color: inherit;
    }
    .search-box {
      position: relative;
      display: flex;
      align-items: center;
      min-width: 240px;
      flex: 1;
      max-width: 340px;
    }
    .search-icon {
      position: absolute;
      left: 10px;
      font-size: 12px;
      color: var(--text-muted);
      pointer-events: none;
    }
    .search-input {
      width: 100%;
      background-color: #0d1117;
      color: var(--text-main);
      border: 1px solid var(--card-border);
      border-radius: 8px;
      padding: 6px 30px 6px 30px;
      font-size: 12.5px;
      outline: none;
      transition: border-color 0.15s ease;
    }
    .search-input::placeholder {
      color: var(--text-muted);
    }
    .search-input:focus {
      border-color: var(--accent);
    }
    .search-clear {
      display: none;
      position: absolute;
      right: 6px;
      background: transparent;
      border: none;
      color: var(--text-muted);
      padding: 2px 6px;
      font-size: 12px;
      border-radius: 4px;
    }
    .search-clear:hover {
      color: var(--text-main);
      background: #21262d;
    }

    /* Game Cards Grid */
    .game-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(360px, 1fr));
      gap: 16px;
    }
    .game-card {
      background-color: var(--card-bg);
      border: 1px solid var(--card-border);
      border-radius: 10px;
      padding: 16px;
      display: flex;
      flex-direction: column;
      gap: 10px;
      position: relative;
      transition: transform 0.15s ease, border-color 0.15s ease;
    }
    .game-card:hover {
      border-color: #58a6ff55;
      transform: translateY(-2px);
    }
    .card-top {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 10px;
    }
    .game-title {
      font-size: 16px;
      font-weight: 600;
      line-height: 1.3;
    }
    .badges {
      display: flex;
      gap: 6px;
      flex-wrap: wrap;
      margin-top: 4px;
    }
    .badge {
      font-size: 11px;
      font-weight: 600;
      padding: 2px 8px;
      border-radius: 12px;
      text-transform: uppercase;
    }
    .badge-steam { background: #1b2838; color: #66c0f4; border: 1px solid #66c0f455; }
    .badge-heroic { background: #33152c; color: #f47bcf; border: 1px solid #f47bcf55; }
    .badge-lutris { background: #38240f; color: #f5a623; border: 1px solid #f5a62355; }
    .badge-engine { background: #21262d; color: var(--text-muted); border: 1px solid var(--card-border); }

    .status-pill {
      font-size: 11px;
      font-weight: 700;
      padding: 4px 10px;
      border-radius: 6px;
      white-space: nowrap;
    }
    .status-cached { background: rgba(35, 134, 54, 0.2); color: #3fb950; border: 1px solid #3fb95055; }
    .status-uncompiled { background: rgba(210, 153, 34, 0.2); color: #d29922; border: 1px solid #d2992255; }
    .status-compiling { background: rgba(88, 166, 255, 0.2); color: #58a6ff; border: 1px solid #58a6ff55; animation: pulse 1.5s infinite; }

    @keyframes pulse {
      0% { opacity: 0.7; }
      50% { opacity: 1; }
      100% { opacity: 0.7; }
    }

    .meta-row {
      display: flex;
      justify-content: space-between;
      font-size: 12px;
      color: var(--text-muted);
    }
    .meta-row strong { color: var(--text-main); }

    /* Progress Bar */
    .progress-box {
      display: none;
      flex-direction: column;
      gap: 6px;
    }
    .progress-track {
      width: 100%;
      height: 8px;
      background: #21262d;
      border-radius: 4px;
      overflow: hidden;
    }
    .progress-fill {
      height: 100%;
      width: 0%;
      background: linear-gradient(90deg, #388bfd, #a371f7);
      border-radius: 4px;
      transition: width 0.15s ease;
    }
    .progress-text {
      font-size: 11px;
      color: var(--text-muted);
      display: flex;
      justify-content: space-between;
    }

    .card-actions {
      display: flex;
      justify-content: flex-end;
      margin-top: 4px;
    }

    /* Loading Spinner */
    .spinner {
      display: inline-block;
      width: 20px;
      height: 20px;
      border: 3px solid rgba(255,255,255,.2);
      border-radius: 50%;
      border-top-color: #58a6ff;
      animation: spin 1s ease-in-out infinite;
    }
    @keyframes spin {
      to { transform: rotate(360deg); }
    }

    /* Diagnostics Modal */
    .modal-backdrop {
      display: none;
      position: fixed;
      inset: 0;
      background: rgba(1, 4, 9, 0.78);
      z-index: 1000;
      align-items: center;
      justify-content: center;
      padding: 24px;
    }
    .modal-box {
      background: var(--card-bg);
      border: 1px solid var(--card-border);
      border-radius: 12px;
      width: 100%;
      max-width: 920px;
      max-height: 85vh;
      display: flex;
      flex-direction: column;
      overflow: hidden;
      box-shadow: 0 16px 48px rgba(0,0,0,0.6);
    }
    .modal-header {
      padding: 16px 20px;
      border-bottom: 1px solid var(--card-border);
      display: flex;
      justify-content: space-between;
      align-items: center;
    }
    .modal-header h2 { font-size: 16px; font-weight: 700; }
    .modal-body {
      padding: 20px;
      overflow-y: auto;
      display: flex;
      flex-direction: column;
      gap: 18px;
      font-size: 13px;
    }
    .diag-source-block {
      border: 1px solid var(--card-border);
      border-radius: 8px;
      padding: 14px;
      background: #0d1117;
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .diag-source-title {
      font-size: 14px;
      font-weight: 700;
      color: var(--accent);
      text-transform: uppercase;
      letter-spacing: 0.6px;
    }
    .diag-list {
      display: flex;
      flex-direction: column;
      gap: 6px;
      padding-left: 8px;
    }
    .diag-item {
      font-size: 12px;
      line-height: 1.45;
      color: var(--text-muted);
      word-break: break-all;
    }
    .diag-item strong { color: var(--text-main); }
    .tag-found { color: #3fb950; font-weight: 700; }
    .tag-missing { color: #8b949e; }
    .tag-skipped { color: #d29922; font-weight: 600; }
    .status-stale {
      background: rgba(210, 153, 34, 0.16);
      color: #e3b341;
      border: 1px solid rgba(210, 153, 34, 0.45);
    }
    .setting-row {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 18px;
      background: rgba(255, 255, 255, 0.02);
      border: 1px solid var(--border);
      border-radius: 10px;
      padding: 14px 16px;
    }
    .setting-info h3 {
      margin: 0 0 4px 0;
      font-size: 14px;
      color: var(--text-main);
    }
    .setting-info p {
      margin: 0;
      font-size: 12px;
      color: var(--text-muted);
      line-height: 1.45;
    }
    .toggle-switch {
      position: relative;
      display: inline-block;
      width: 46px;
      height: 24px;
      flex-shrink: 0;
    }
    .toggle-switch input {
      opacity: 0;
      width: 0;
      height: 0;
    }
    .toggle-slider {
      position: absolute;
      cursor: pointer;
      inset: 0;
      background-color: #30363d;
      border: 1px solid var(--border);
      transition: .2s;
      border-radius: 24px;
    }
    .toggle-slider:before {
      position: absolute;
      content: "";
      height: 18px;
      width: 18px;
      left: 2px;
      bottom: 2px;
      background-color: #c9d1d9;
      transition: .2s;
      border-radius: 50%;
    }
    .toggle-switch input:checked + .toggle-slider {
      background-color: rgba(63, 185, 80, 0.35);
      border-color: #3fb950;
    }
    .toggle-switch input:checked + .toggle-slider:before {
      transform: translateX(22px);
      background-color: #3fb950;
    }
    .setting-controls {
      display: flex;
      align-items: center;
      gap: 8px;
      flex-shrink: 0;
    }
    .cache-dropdown {
      position: relative;
      user-select: none;
    }
    .cache-dropdown-btn {
      min-width: 140px;
      height: 36px;
      justify-content: space-between;
      background: linear-gradient(180deg, #21262d 0%, #161b22 100%);
      color: var(--text-main);
      border: 1px solid var(--card-border);
      border-radius: 8px;
      padding: 0 12px;
      font-size: 13px;
      font-weight: 600;
      box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.04);
      transition: all 0.15s ease;
    }
    .cache-dropdown-btn:hover {
      background: linear-gradient(180deg, #292f38 0%, #1c2128 100%);
      border-color: #484f58;
    }
    .cache-dropdown.open .cache-dropdown-btn {
      background: #1c2128;
      border-color: var(--accent);
      box-shadow: 0 0 0 3px rgba(88, 166, 255, 0.16);
    }
    .cache-dropdown-label {
      display: inline-flex;
      align-items: center;
      gap: 7px;
      color: var(--text-main);
    }
    .cache-dropdown-dot {
      width: 7px;
      height: 7px;
      border-radius: 50%;
      background: linear-gradient(135deg, #a371f7, #58a6ff);
      box-shadow: 0 0 6px rgba(88, 166, 255, 0.55);
      flex-shrink: 0;
    }
    .cache-dropdown-chevron {
      display: inline-flex;
      align-items: center;
      color: var(--text-muted);
      transition: transform 0.18s ease, color 0.18s ease;
    }
    .cache-dropdown.open .cache-dropdown-chevron {
      transform: rotate(180deg);
      color: var(--accent);
    }
    .cache-dropdown-menu {
      display: none;
      position: absolute;
      bottom: calc(100% + 8px);
      right: 0;
      min-width: 196px;
      background: #161b22;
      border: 1px solid #3d444d;
      border-radius: 10px;
      padding: 6px;
      box-shadow: 0 14px 36px rgba(0, 0, 0, 0.68), 0 0 0 1px rgba(255, 255, 255, 0.03);
      z-index: 1050;
      flex-direction: column;
      gap: 2px;
    }
    .cache-dropdown.open .cache-dropdown-menu {
      display: flex;
    }
    .cache-dropdown-item {
      width: 100%;
      background: transparent;
      color: var(--text-main);
      border: 1px solid transparent;
      border-radius: 7px;
      padding: 7px 10px;
      font-size: 12.5px;
      font-weight: 600;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
      text-align: left;
    }
    .cache-dropdown-item:hover {
      background: #21262d;
      color: #fff;
    }
    .cache-dropdown-item.active {
      background: rgba(88, 166, 255, 0.14);
      color: #79c0ff;
      border-color: rgba(88, 166, 255, 0.35);
    }
    .cache-dropdown-item-left {
      display: inline-flex;
      align-items: center;
      gap: 7px;
    }
    .cache-dropdown-tag {
      font-size: 10px;
      font-weight: 600;
      padding: 1px 6px;
      border-radius: 8px;
      background: rgba(255, 255, 255, 0.06);
      color: var(--text-muted);
    }
    .cache-dropdown-item.active .cache-dropdown-tag {
      background: rgba(88, 166, 255, 0.2);
      color: #79c0ff;
    }
    .cache-dropdown-check {
      opacity: 0;
      color: #79c0ff;
      font-size: 12px;
      font-weight: 700;
    }
    .cache-dropdown-item.active .cache-dropdown-check {
      opacity: 1;
    }
    .cache-dropdown-divider {
      height: 1px;
      background: var(--card-border);
      margin: 4px 2px;
    }
    .custom-cache-box {
      display: none;
      align-items: center;
      background: #0d1117;
      border: 1px solid var(--card-border);
      border-radius: 8px;
      padding: 0 10px 0 6px;
      height: 36px;
      transition: border-color 0.15s ease, box-shadow 0.15s ease;
    }
    .custom-cache-box:focus-within {
      border-color: var(--accent);
      box-shadow: 0 0 0 3px rgba(88, 166, 255, 0.16);
    }
    .setting-number {
      -webkit-appearance: none;
      appearance: none;
      background: transparent;
      color: var(--text-main);
      border: none;
      width: 54px;
      padding: 6px 6px;
      font-size: 13px;
      font-weight: 600;
      text-align: right;
      outline: none;
    }
    .setting-number::-webkit-inner-spin-button,
    .setting-number::-webkit-outer-spin-button {
      -webkit-appearance: none;
      margin: 0;
    }
    .custom-cache-unit {
      font-size: 11.5px;
      color: var(--text-muted);
      font-weight: 700;
      letter-spacing: 0.4px;
      user-select: none;
    }
    .crypto-list {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .crypto-card {
      background: #0d1117;
      border: 1px solid var(--card-border);
      border-radius: 8px;
      padding: 12px 14px;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .crypto-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 8px;
    }
    .crypto-coin {
      font-size: 13px;
      font-weight: 700;
      color: var(--text-main);
    }
    .crypto-net {
      font-size: 11px;
      font-weight: 600;
      padding: 2px 8px;
      border-radius: 10px;
      background: #21262d;
      color: var(--text-muted);
      border: 1px solid var(--card-border);
    }
    .crypto-net-warn {
      background: rgba(210, 153, 34, 0.18);
      color: #e3b341;
      border-color: rgba(210, 153, 34, 0.45);
    }
    .crypto-addr-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
    }
    .crypto-addr {
      font-family: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, monospace;
      font-size: 12px;
      color: #c9d1d9;
      background: rgba(255, 255, 255, 0.03);
      border: 1px solid rgba(48, 54, 61, 0.8);
      border-radius: 6px;
      padding: 6px 10px;
      flex: 1;
      word-break: break-all;
      user-select: all;
    }
    .btn-copy {
      padding: 6px 12px;
      font-size: 12px;
      flex-shrink: 0;
      min-width: 78px;
      justify-content: center;
    }
    .btn-copy.copied {
      background-color: rgba(63, 185, 80, 0.2);
      color: #3fb950;
      border-color: #3fb950;
    }
    .support-warning {
      background: rgba(210, 153, 34, 0.12);
      border: 1px solid rgba(210, 153, 34, 0.4);
      border-radius: 8px;
      padding: 10px 14px;
      font-size: 12px;
      color: #e3b341;
      line-height: 1.45;
    }
    .support-upstream {
      background: rgba(88, 166, 255, 0.08);
      border: 1px solid rgba(88, 166, 255, 0.3);
      border-radius: 8px;
      padding: 12px 14px;
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 12px;
      flex-wrap: wrap;
    }
  </style>
</head>
<body>
  <header>
    <div class="brand">
      <div class="brand-icon"><svg viewBox="130 58 252 396" width="14" height="22"><path fill="#fff" stroke="#fff" stroke-width="10" stroke-linejoin="round" d="M296 64 L136 288 H240 L208 448 L376 208 H272 Z"/></svg></div>
      <div>
        <h1>Shaderless</h1>
        <div class="brand-subtitle">Unofficial Linux port of SCSKiller · v{{VERSION}}</div>
      </div>
    </div>

    <div class="driver-pill" id="driver-pill">
      <div class="status-dot"></div>
      <div class="driver-meta">Scanning GPU & Driver...</div>
    </div>

    <div class="header-actions">
      <button class="btn-secondary" id="btn-rescan" onclick="scanGames()">🔄 Rescan</button>
      <button class="btn-secondary" id="btn-diagnose" onclick="openDiagnose()" title="Inspect all checked library paths, discovered games, and skipped manifests">🩺 Diagnose</button>
      <button class="btn-secondary" id="btn-settings" onclick="openSettings()" title="Configure automation options (all off by default)">⚙️ Settings</button>
      <button class="btn-support" id="btn-support" onclick="openSupport()" title="Support Shaderless">💜 Support</button>
      <button class="btn-primary" onclick="compileAll()">⚡ Pre-Compile All</button>
      <button class="btn-danger" onclick="clearCache()" title="Purge ALL compiled shaders from GPU driver and Steam game caches">🗑️ Clear All Caches</button>
    </div>
  </header>

  <main>
    <div class="section-title">
      <span id="library-count">Installed Games</span>
      <span style="font-size: 11px; color: var(--text-muted);">Shaders are compiled directly into GPU driver cache ahead of time</span>
    </div>

    <div class="filter-bar" id="filter-bar">
      <div class="filter-pills">
        <button class="filter-pill active-all" id="flt-all" onclick="setLauncherFilter('all')">
          <span>All</span><span class="filter-count" id="cnt-all">0</span>
        </button>
        <button class="filter-pill" id="flt-steam" onclick="setLauncherFilter('steam')">
          <span>Steam</span><span class="filter-count" id="cnt-steam">0</span>
        </button>
        <button class="filter-pill" id="flt-heroic" onclick="setLauncherFilter('heroic')">
          <span>Heroic</span><span class="filter-count" id="cnt-heroic">0</span>
        </button>
        <button class="filter-pill" id="flt-lutris" onclick="setLauncherFilter('lutris')">
          <span>Lutris</span><span class="filter-count" id="cnt-lutris">0</span>
        </button>
        <div class="filter-divider"></div>
        <button class="filter-pill" id="flt-needs" onclick="toggleNeedsCompileFilter()" title="Show only games that need pre-compilation, are stale, or have newly recorded pipelines">
          <span>⚡ Needs Compile</span><span class="filter-count" id="cnt-needs">0</span>
        </button>
      </div>

      <div class="search-box">
        <span class="search-icon">🔍</span>
        <input type="text" class="search-input" id="search-input" placeholder="Search game, engine, or ID... (/)" oninput="onSearchInput(this.value)" />
        <button class="search-clear" id="btn-clear-search" onclick="clearSearch()" title="Clear search">✕</button>
      </div>
    </div>

    <div class="game-grid" id="game-grid">
      <div style="color: var(--text-muted); padding: 40px; display: flex; align-items: center; gap: 12px;">
        <div class="spinner"></div>
        <span>Scanning game libraries (Steam, Heroic, Lutris)...</span>
      </div>
    </div>
  </main>

  <div class="modal-backdrop" id="diag-modal" onclick="if(event.target === this) closeDiagnose()">
    <div class="modal-box">
      <div class="modal-header">
        <h2>🩺 Library Discovery Diagnostics (Native, XDG & Flatpak)</h2>
        <button class="btn-secondary" style="padding: 4px 10px; font-size: 12px;" onclick="closeDiagnose()">✕ Close</button>
      </div>
      <div class="modal-body" id="diag-body">
        <div style="color: var(--text-muted); display: flex; align-items: center; gap: 10px;">
          <div class="spinner"></div>
          <span>Inspecting launcher directories and manifests...</span>
        </div>
      </div>
    </div>
  </div>

  <div class="modal-backdrop" id="settings-modal" onclick="if(event.target === this) closeSettings()">
    <div class="modal-box" style="max-width: 620px;">
      <div class="modal-header">
        <h2>⚙️ Settings ("Set and Forget")</h2>
        <button class="btn-secondary" style="padding: 4px 10px; font-size: 12px;" onclick="closeSettings()">✕ Close</button>
      </div>
      <div class="modal-body">
        <div class="setting-row">
          <div class="setting-info">
            <h3>Record all games automatically <span style="font-size: 11px; color: var(--text-muted); font-weight: 400;">(Default: OFF)</span></h3>
            <p>Automatically install the pipeline recorder on eligible games when scanning so you don't need to click Record on each one. Games with anti-cheat are always excluded, and you can still toggle Record per game.</p>
          </div>
          <label class="toggle-switch">
            <input type="checkbox" id="chk-auto-record" onchange="saveSettings()">
            <span class="toggle-slider"></span>
          </label>
        </div>

        <div class="setting-row">
          <div class="setting-info">
            <h3>Compile automatically after the game closes <span style="font-size: 11px; color: var(--text-muted); font-weight: 400;">(Default: OFF)</span></h3>
            <p>Watch for a running game to close, import newly recorded pipelines from your session, and run a fast incremental compile in the background when no other game is open.</p>
          </div>
          <label class="toggle-switch">
            <input type="checkbox" id="chk-auto-compile" onchange="saveSettings()">
            <span class="toggle-slider"></span>
          </label>
        </div>

        <div class="setting-row">
          <div class="setting-info">
            <h3>Max shader cache size <span style="font-size: 11px; color: var(--text-muted); font-weight: 400;">(Default: 10 GB)</span></h3>
            <p>Raises the GPU driver shader cache cap (NVIDIA &amp; AMD Mesa) so large UE5 games never evict older shaders. On NVIDIA, Shaderless also splits caches into &lt;1.85 GB lock-free multi-part files automatically so caches never truncate at 2 GB.</p>
          </div>
          <div class="setting-controls">
            <div class="cache-dropdown" id="cache-dropdown" data-value="10">
              <button type="button" class="cache-dropdown-btn" id="btn-max-cache" onclick="toggleMaxCacheDropdown(event)" aria-haspopup="listbox" aria-expanded="false">
                <span class="cache-dropdown-label">
                  <span class="cache-dropdown-dot"></span>
                  <span id="lbl-max-cache">10 GB</span>
                </span>
                <span class="cache-dropdown-chevron">
                  <svg width="12" height="12" viewBox="0 0 12 12" fill="none"><path d="M2.5 4.5L6 8L9.5 4.5" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"/></svg>
                </span>
              </button>
              <div class="cache-dropdown-menu" id="menu-max-cache" role="listbox">
                <button type="button" class="cache-dropdown-item active" data-val="10" onclick="selectMaxCacheOption('10')">
                  <span class="cache-dropdown-item-left"><span>10 GB</span><span class="cache-dropdown-tag">Default</span></span>
                  <span class="cache-dropdown-check">✓</span>
                </button>
                <button type="button" class="cache-dropdown-item" data-val="25" onclick="selectMaxCacheOption('25')">
                  <span class="cache-dropdown-item-left"><span>25 GB</span></span>
                  <span class="cache-dropdown-check">✓</span>
                </button>
                <button type="button" class="cache-dropdown-item" data-val="50" onclick="selectMaxCacheOption('50')">
                  <span class="cache-dropdown-item-left"><span>50 GB</span></span>
                  <span class="cache-dropdown-check">✓</span>
                </button>
                <button type="button" class="cache-dropdown-item" data-val="100" onclick="selectMaxCacheOption('100')">
                  <span class="cache-dropdown-item-left"><span>100 GB</span></span>
                  <span class="cache-dropdown-check">✓</span>
                </button>
                <div class="cache-dropdown-divider"></div>
                <button type="button" class="cache-dropdown-item" data-val="custom" onclick="selectMaxCacheOption('custom')">
                  <span class="cache-dropdown-item-left"><span>Custom...</span><span class="cache-dropdown-tag">5–500 GB</span></span>
                  <span class="cache-dropdown-check">✓</span>
                </button>
              </div>
            </div>
            <div id="custom-max-cache-wrap" class="custom-cache-box">
              <input type="text" inputmode="numeric" id="inp-max-cache-custom" class="setting-number" maxlength="3" value="10" onchange="onMaxCacheCustomChange()" onkeydown="if(event.key === 'Enter') { event.preventDefault(); this.blur(); }">
              <span class="custom-cache-unit">GB</span>
            </div>
          </div>
        </div>

        <div style="font-size: 11px; color: var(--text-muted); padding: 4px 2px;">
          Saved in <code>~/.local/share/shaderless/settings.json</code>
        </div>
      </div>
    </div>
  </div>

  <div class="modal-backdrop" id="support-modal" onclick="if(event.target === this) closeSupport()">
    <div class="modal-box" style="max-width: 640px;">
      <div class="modal-header">
        <h2>💜 Support Shaderless</h2>
        <button class="btn-secondary" style="padding: 4px 10px; font-size: 12px;" onclick="closeSupport()">✕ Close</button>
      </div>
      <div class="modal-body" style="gap: 14px;">
        <div style="color: var(--text-muted); line-height: 1.5;">
          <strong style="color: var(--text-main);">Shaderless is free and stays free — no paid features, ever.</strong><br/>
          If it saved your games from stutter and you'd like to support Shaderless:
        </div>

        <div class="crypto-list">
          <div class="crypto-card">
            <div class="crypto-header">
              <span class="crypto-coin">Bitcoin (BTC)</span>
              <span class="crypto-net">Network: Bitcoin (SegWit)</span>
            </div>
            <div class="crypto-addr-row">
              <code class="crypto-addr">bc1q3hefk7fswsgq5zxed05vy0v4rt2qks90en5mjp</code>
              <button class="btn-secondary btn-copy" onclick="copyAddress(this, 'bc1q3hefk7fswsgq5zxed05vy0v4rt2qks90en5mjp')">📋 Copy</button>
            </div>
          </div>

          <div class="crypto-card">
            <div class="crypto-header">
              <span class="crypto-coin">Solana (SOL)</span>
              <span class="crypto-net">Network: Solana</span>
            </div>
            <div class="crypto-addr-row">
              <code class="crypto-addr">cQiSktSJbtLNEpZZwsKw8NxEpdXeLKTKXDnCccS6f9g</code>
              <button class="btn-secondary btn-copy" onclick="copyAddress(this, 'cQiSktSJbtLNEpZZwsKw8NxEpdXeLKTKXDnCccS6f9g')">📋 Copy</button>
            </div>
          </div>

          <div class="crypto-card">
            <div class="crypto-header">
              <span class="crypto-coin">Litecoin (LTC)</span>
              <span class="crypto-net">Network: Litecoin</span>
            </div>
            <div class="crypto-addr-row">
              <code class="crypto-addr">LLdkGKhNdWJwRjHm4RERn9qnEuCzRBAnZK</code>
              <button class="btn-secondary btn-copy" onclick="copyAddress(this, 'LLdkGKhNdWJwRjHm4RERn9qnEuCzRBAnZK')">📋 Copy</button>
            </div>
          </div>

          <div class="crypto-card">
            <div class="crypto-header">
              <span class="crypto-coin">USDT (Tether)</span>
              <span class="crypto-net crypto-net-warn">⚠️ Tron (TRC-20) ONLY</span>
            </div>
            <div class="crypto-addr-row">
              <code class="crypto-addr">TStfK2yMPrV5xzXffGaaoMd2CU6RDHTvbg</code>
              <button class="btn-secondary btn-copy" onclick="copyAddress(this, 'TStfK2yMPrV5xzXffGaaoMd2CU6RDHTvbg')">📋 Copy</button>
            </div>
          </div>
        </div>

        <div class="support-warning">
          ⚠️ <strong>Network check:</strong> Send only the listed coin on the listed network. For example, USDT sent on Ethereum (ERC-20) to the Tron (TRC-20) address is permanently lost.
        </div>

        <div class="support-upstream">
          <div style="font-size: 12px; color: var(--text-muted); line-height: 1.45; flex: 1; min-width: 240px;">
            You can also help by starring <strong style="color: var(--text-main);">Shaderless</strong> on GitHub or sharing feedback:
          </div>
          <div style="display: flex; gap: 8px; flex-wrap: wrap;">
            <button class="btn-secondary" style="font-size: 12px; padding: 6px 12px;" onclick="openExternalUrl('https://github.com/cyberps96/Shaderless')">⭐ Star on GitHub</button>
          </div>
        </div>
      </div>
    </div>
  </div>

  <script>
    let currentGames = [];
    let currentSettings = { autoRecordAllGames: false, autoCompileOnExit: false, maxCacheSizeGb: 10 };
    const MAX_CACHE_PRESETS = [10, 25, 50, 100];

    function clampMaxCacheGb(val) {
      const n = parseInt(val, 10);
      if (isNaN(n) || n <= 0) return 10;
      return Math.max(5, Math.min(500, n));
    }

    function closeMaxCacheDropdown() {
      const dd = document.getElementById('cache-dropdown');
      const btn = document.getElementById('btn-max-cache');
      if (dd) dd.classList.remove('open');
      if (btn) btn.setAttribute('aria-expanded', 'false');
    }

    function toggleMaxCacheDropdown(e) {
      if (e) e.stopPropagation();
      const dd = document.getElementById('cache-dropdown');
      const btn = document.getElementById('btn-max-cache');
      if (!dd) return;
      const willOpen = !dd.classList.contains('open');
      dd.classList.toggle('open', willOpen);
      if (btn) btn.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
    }

    function updateMaxCacheDropdownUi(modeVal) {
      const dd = document.getElementById('cache-dropdown');
      const lbl = document.getElementById('lbl-max-cache');
      const menu = document.getElementById('menu-max-cache');
      if (!dd || !lbl || !menu) return;
      dd.dataset.value = modeVal;
      lbl.innerText = modeVal === 'custom' ? 'Custom' : `${modeVal} GB`;
      for (const item of menu.querySelectorAll('.cache-dropdown-item')) {
        item.classList.toggle('active', item.dataset.val === modeVal);
      }
    }

    function syncMaxCacheControls(gb) {
      const clamped = clampMaxCacheGb(gb);
      const wrap = document.getElementById('custom-max-cache-wrap');
      const inp = document.getElementById('inp-max-cache-custom');
      if (!wrap || !inp) return;
      inp.value = String(clamped);
      if (MAX_CACHE_PRESETS.includes(clamped)) {
        updateMaxCacheDropdownUi(String(clamped));
        wrap.style.display = 'none';
      } else {
        updateMaxCacheDropdownUi('custom');
        wrap.style.display = 'inline-flex';
      }
    }

    function getSelectedMaxCacheGb() {
      const dd = document.getElementById('cache-dropdown');
      const inp = document.getElementById('inp-max-cache-custom');
      if (!dd) return clampMaxCacheGb(currentSettings.maxCacheSizeGb);
      const modeVal = dd.dataset.value || '10';
      if (modeVal === 'custom') {
        return clampMaxCacheGb(inp ? inp.value : currentSettings.maxCacheSizeGb);
      }
      return clampMaxCacheGb(modeVal);
    }

    function selectMaxCacheOption(val) {
      closeMaxCacheDropdown();
      const wrap = document.getElementById('custom-max-cache-wrap');
      const inp = document.getElementById('inp-max-cache-custom');
      if (!wrap || !inp) return;
      updateMaxCacheDropdownUi(val);
      if (val === 'custom') {
        wrap.style.display = 'inline-flex';
        inp.value = String(clampMaxCacheGb(currentSettings.maxCacheSizeGb));
        inp.focus();
        inp.select();
        return;
      }
      wrap.style.display = 'none';
      inp.value = val;
      saveSettings();
    }

    function onMaxCacheCustomChange() {
      const inp = document.getElementById('inp-max-cache-custom');
      if (!inp) return;
      const clamped = clampMaxCacheGb(inp.value);
      inp.value = String(clamped);
      saveSettings();
    }
    let activeLauncherFilter = 'all';
    let onlyNeedsCompileFilter = false;
    let searchQuery = '';

    function escapeHtml(str) {
      return String(str ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
    }

    function getGameSourceKey(g) {
      if (g.source === 0 || g.source === 'Steam') return 'steam';
      if (g.source === 1 || g.source === 'Heroic') return 'heroic';
      if (g.source === 2 || g.source === 'Lutris') return 'lutris';
      return 'other';
    }

    function gameNeedsAction(g) {
      const isCached = g.state === 1 || g.state === 'Cached';
      return !isCached || (g.newRecordedPsos > 0);
    }

    function getFilteredGames() {
      const q = (searchQuery || '').trim().toLowerCase();
      return (currentGames || []).filter(g => {
        const srcKey = getGameSourceKey(g);
        if (activeLauncherFilter !== 'all' && srcKey !== activeLauncherFilter) return false;
        if (onlyNeedsCompileFilter && !gameNeedsAction(g)) return false;
        if (q.length > 0) {
          const name = (g.name || '').toLowerCase();
          const id = (g.id || '').toLowerCase();
          const engine = getEngineName(g.engine).toLowerCase();
          const fp = g.isFlatpak ? 'flatpak' : '';
          if (!name.includes(q) && !id.includes(q) && !engine.includes(q) && !srcKey.includes(q) && !fp.includes(q)) {
            return false;
          }
        }
        return true;
      });
    }

    function updateFilterBarUI(filteredCount) {
      const all = currentGames || [];
      const steamCnt = all.filter(g => getGameSourceKey(g) === 'steam').length;
      const heroicCnt = all.filter(g => getGameSourceKey(g) === 'heroic').length;
      const lutrisCnt = all.filter(g => getGameSourceKey(g) === 'lutris').length;
      const needsCnt = all.filter(g => gameNeedsAction(g)).length;

      const setTxt = (id, val) => { const el = document.getElementById(id); if (el) el.innerText = val; };
      setTxt('cnt-all', all.length);
      setTxt('cnt-steam', steamCnt);
      setTxt('cnt-heroic', heroicCnt);
      setTxt('cnt-lutris', lutrisCnt);
      setTxt('cnt-needs', needsCnt);

      const launchers = ['all', 'steam', 'heroic', 'lutris'];
      for (const key of launchers) {
        const btn = document.getElementById(`flt-${key}`);
        if (!btn) continue;
        btn.className = activeLauncherFilter === key ? `filter-pill active-${key}` : 'filter-pill';
      }

      const needsBtn = document.getElementById('flt-needs');
      if (needsBtn) {
        needsBtn.className = onlyNeedsCompileFilter ? 'filter-pill active-needs' : 'filter-pill';
      }

      const clrBtn = document.getElementById('btn-clear-search');
      if (clrBtn) {
        clrBtn.style.display = (searchQuery && searchQuery.length > 0) ? 'inline-flex' : 'none';
      }

      const isFiltered = activeLauncherFilter !== 'all' || onlyNeedsCompileFilter || (searchQuery && searchQuery.trim().length > 0);
      const countLabel = document.getElementById('library-count');
      if (countLabel) {
        countLabel.innerText = isFiltered
          ? `Showing ${filteredCount} of ${all.length} Installed Games`
          : `Installed Games (${all.length})`;
      }
    }

    function setLauncherFilter(launcher) {
      activeLauncherFilter = launcher;
      renderGames();
    }

    function toggleNeedsCompileFilter() {
      onlyNeedsCompileFilter = !onlyNeedsCompileFilter;
      renderGames();
    }

    function onSearchInput(val) {
      searchQuery = val || '';
      renderGames();
    }

    function clearSearch() {
      searchQuery = '';
      const input = document.getElementById('search-input');
      if (input) input.value = '';
      renderGames();
    }

    function resetAllFilters() {
      activeLauncherFilter = 'all';
      onlyNeedsCompileFilter = false;
      clearSearch();
    }

    function post(data) {
      if (window.external && window.external.sendMessage) {
        window.external.sendMessage(JSON.stringify(data));
      }
    }

    function scanGames() {
      const btn = document.getElementById('btn-rescan');
      if (btn) btn.innerText = '🔄 Scanning...';
      post({ action: 'scan' });
    }

    function openDiagnose() {
      const modal = document.getElementById('diag-modal');
      const body = document.getElementById('diag-body');
      modal.style.display = 'flex';
      body.innerHTML = '<div style="color: var(--text-muted); display: flex; align-items: center; gap: 10px;"><div class="spinner"></div><span>Inspecting launcher directories and manifests...</span></div>';
      post({ action: 'diagnose' });
    }

    function closeDiagnose() {
      document.getElementById('diag-modal').style.display = 'none';
    }

    function openSettings() {
      closeMaxCacheDropdown();
      document.getElementById('chk-auto-record').checked = !!currentSettings.autoRecordAllGames;
      document.getElementById('chk-auto-compile').checked = !!currentSettings.autoCompileOnExit;
      syncMaxCacheControls(currentSettings.maxCacheSizeGb || 10);
      document.getElementById('settings-modal').style.display = 'flex';
    }

    function closeSettings() {
      closeMaxCacheDropdown();
      document.getElementById('settings-modal').style.display = 'none';
    }

    function openSupport() {
      document.getElementById('support-modal').style.display = 'flex';
    }

    function closeSupport() {
      document.getElementById('support-modal').style.display = 'none';
    }

    function openExternalUrl(url) {
      post({ action: 'open-url', url });
    }

    function copyAddress(btn, text) {
      post({ action: 'copy-text', text });

      const markCopied = () => {
        if (!btn) return;
        const prev = btn.dataset.origLabel || '📋 Copy';
        btn.dataset.origLabel = prev;
        btn.classList.add('copied');
        btn.innerText = '✓ Copied!';
        if (btn._copyTimer) clearTimeout(btn._copyTimer);
        btn._copyTimer = setTimeout(() => {
          btn.classList.remove('copied');
          btn.innerText = prev;
        }, 1800);
      };

      const fallbackCopy = () => {
        const ta = document.createElement('textarea');
        ta.value = text;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.focus();
        ta.select();
        try { document.execCommand('copy'); } catch (e) {}
        document.body.removeChild(ta);
        markCopied();
      };

      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(markCopied).catch(fallbackCopy);
      } else {
        fallbackCopy();
      }
    }

    document.addEventListener('click', (e) => {
      const dd = document.getElementById('cache-dropdown');
      if (dd && dd.classList.contains('open') && !dd.contains(e.target)) {
        closeMaxCacheDropdown();
      }
    });

    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') {
        const dd = document.getElementById('cache-dropdown');
        if (dd && dd.classList.contains('open')) {
          closeMaxCacheDropdown();
          return;
        }
        const anyModalOpen = ['diag-modal', 'settings-modal', 'support-modal']
          .some(id => document.getElementById(id)?.style.display === 'flex');
        if (anyModalOpen) {
          closeDiagnose();
          closeSettings();
          closeSupport();
        } else if (searchQuery && searchQuery.length > 0) {
          clearSearch();
          document.getElementById('search-input')?.blur();
        }
      } else if (e.key === '/' && document.activeElement?.tagName !== 'INPUT' && document.activeElement?.tagName !== 'TEXTAREA') {
        const anyModalOpen = ['diag-modal', 'settings-modal', 'support-modal']
          .some(id => document.getElementById(id)?.style.display === 'flex');
        if (!anyModalOpen) {
          e.preventDefault();
          document.getElementById('search-input')?.focus();
        }
      }
    });

    function saveSettings() {
      const autoRecord = document.getElementById('chk-auto-record').checked;
      const autoCompile = document.getElementById('chk-auto-compile').checked;
      const maxCacheSizeGb = getSelectedMaxCacheGb();
      currentSettings = { autoRecordAllGames: autoRecord, autoCompileOnExit: autoCompile, maxCacheSizeGb };
      post({
        action: 'save-settings',
        autoRecordAllGames: autoRecord,
        autoCompileOnExit: autoCompile,
        maxCacheSizeGb
      });
    }

    function compileGame(id) {
      updateProgress({ gameId: id, percentage: 0, currentAction: 'Starting...', isComplete: false });
      post({ action: 'compile', gameId: id });
    }

    function compileAll() {
      post({ action: 'compile-all' });
    }

    function toggleRecorder(id) {
      post({ action: 'toggle-recorder', gameId: id });
    }

    function clearCache() {
      if (confirm('Purge ALL compiled shaders from GPU driver and Steam game caches?\n\nThis will reset cache for all games to 0 B.')) {
        for (const k of Object.keys(recentProgress)) delete recentProgress[k];
        post({ action: 'clear-all' });
      }
    }

    function clearGameCache(id, name) {
      if (confirm(`Delete on-disk shader cache for "${name}"?\n\nThis will reset the game's shader status to Needs Pre-Compilation (0 B).`)) {
        delete recentProgress[id];
        post({ action: 'clear-game', gameId: id });
      }
    }

    function formatBytes(bytes) {
      if (!bytes || bytes === 0) return '0 B';
      const k = 1024;
      const dm = 2;
      const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
      const i = Math.floor(Math.log(bytes) / Math.log(k));
      return parseFloat((bytes / Math.pow(k, i)).toFixed(dm)) + ' ' + sizes[i];
    }

    function onMessage(raw) {
      try {
        const msg = (typeof raw === 'string') ? JSON.parse(raw) : raw;
        if (msg.type === 'state') {
          updateState(msg);
          post({ action: 'ack' });
        } else if (msg.type === 'progress') {
          updateProgress(msg);
        } else if (msg.type === 'diagnostics') {
          renderDiagnostics(msg.report);
        } else if (msg.type === 'error') {
          alert(msg.message);
        }
      } catch (e) {
        console.error('Error handling message from backend:', e, raw);
      }
    }

    function renderDiagnostics(report) {
      const body = document.getElementById('diag-body');
      if (!report) {
        body.innerHTML = '<div style="color: var(--red);">Failed to load diagnostics report.</div>';
        return;
      }

      const sourceNames = ['Steam', 'Heroic', 'Lutris', 'Bottles', 'Manual'];
      const sourcesHtml = (report.sources || []).map(src => {
        const sName = typeof src.source === 'number' ? (sourceNames[src.source] || 'Launcher') : src.source;
        const pathsHtml = (src.checkedPaths || []).map(p => `
          <div class="diag-item">
            <span class="${p.exists ? 'tag-found' : 'tag-missing'}">${p.exists ? '[FOUND]' : '[MISSING]'}</span>
            <strong>${p.description}:</strong> <code>${p.path}</code>
          </div>
        `).join('') || '<div class="diag-item">(none)</div>';

        const gamesHtml = (src.discoveredGames || []).map(g => `
          <div class="diag-item">
            <span class="tag-found">+ ${g.name}</span>
            <span>[ID: <code>${g.id}</code>${g.isFlatpak ? ' • Flatpak' : ''} • Engine: ${getEngineName(g.engine)} • Cache: ${formatBytes(g.cacheSizeBytes)}]</span><br/>
            <span>Install: <code>${g.installPath}</code> | Exe: <code>${g.executablePath}</code></span>
          </div>
        `).join('') || '<div class="diag-item">(none discovered)</div>';

        const skippedHtml = (src.skippedItems || []).map(s => `
          <div class="diag-item">
            <span class="tag-skipped">- ${s.nameOrId}${s.isFlatpak ? ' [Flatpak]' : ''}:</span> ${s.reason}
            <span style="opacity:0.75;">(<code>${s.path}</code>)</span>
          </div>
        `).join('') || '<div class="diag-item">(none skipped)</div>';

        return `
          <div class="diag-source-block">
            <div class="diag-source-title">${sName}</div>
            ${src.error ? `<div style="color: var(--red); font-size: 12px;">Error: ${src.error}</div>` : ''}
            <div><strong>Checked Paths (${(src.checkedPaths || []).length}):</strong></div>
            <div class="diag-list">${pathsHtml}</div>
            <div><strong>Discovered Games (${(src.discoveredGames || []).length}):</strong></div>
            <div class="diag-list">${gamesHtml}</div>
            <div><strong>Skipped Entries (${(src.skippedItems || []).length}):</strong></div>
            <div class="diag-list">${skippedHtml}</div>
          </div>
        `;
      }).join('');

      body.innerHTML = `
        <div class="diag-source-block">
          <div><strong>Active GPU Driver:</strong> ${report.activeDriverSummary || 'Unknown'}</div>
          <div><strong>Default Proton Runtime:</strong> <code>${report.defaultProtonBinary || 'Not found'}</code></div>
        </div>
        ${sourcesHtml}
      `;
    }

    function updateState(state) {
      const btn = document.getElementById('btn-rescan');
      if (btn) btn.innerText = '🔄 Rescan';

      if (state.settings) {
        currentSettings = state.settings;
        const chkRec = document.getElementById('chk-auto-record');
        const chkComp = document.getElementById('chk-auto-compile');
        if (chkRec) chkRec.checked = !!currentSettings.autoRecordAllGames;
        if (chkComp) chkComp.checked = !!currentSettings.autoCompileOnExit;
        syncMaxCacheControls(currentSettings.maxCacheSizeGb || 10);
      }

      if (state.driver) {
        const d = state.driver;
        const totalMb = formatBytes(d.totalCacheSizeBytes || (d.cacheSizeBytes + (d.steamShaderCacheSizeBytes || 0)));
        document.getElementById('driver-pill').innerHTML = `
          <div class="status-dot"></div>
          <div class="driver-meta">
            <strong>${d.deviceName}</strong> • ${d.driverName} (v${d.driverVersion}) • Total GPU Cache: <strong>${totalMb}</strong>
          </div>
        `;
      }

      if (state.games) {
        currentGames = state.games;
        for (const g of currentGames) {
          const prev = recentProgress[g.id];
          const isCached = g.state === 1 || g.state === 'Cached';
          if (prev && prev.isComplete && !prev.failed && !isCached) {
            delete recentProgress[g.id];
          }
        }
        renderGames();
      }
    }

    const recentProgress = {};

    function applyProgressToCard(card, p) {
      const pBox = card.querySelector('.progress-box');
      const pFill = card.querySelector('.progress-fill');
      const pText = card.querySelector('.progress-text');
      const pStatus = card.querySelector('.status-pill');
      const btn = card.querySelector('.btn-primary');

      pBox.style.display = 'flex';
      pFill.style.width = `${p.percentage}%`;
      const actionColor = p.failed ? 'color: #f85149;' : (p.isComplete ? (p.cacheVerified === false ? 'color: #d29922;' : 'color: #3fb950;') : '');
      const rightLabel = p.isComplete ? (p.failed ? '✗ Failed' : '✓ Done') : `${p.percentage}%`;
      pText.innerHTML = `<span style="${actionColor}">${p.currentAction}</span><span style="${actionColor}">${rightLabel}</span>`;

      if (p.isComplete) {
        btn.disabled = false;
        if (p.failed) {
          pStatus.className = 'status-pill status-uncompiled';
          pStatus.innerText = 'Action Needed';
        } else if (p.cacheVerified === false) {
          pStatus.className = 'status-pill status-uncompiled';
          pStatus.innerText = "Couldn't Confirm";
          btn.innerText = '⚡ Re-Compile';
        } else {
          pStatus.className = 'status-pill status-cached';
          pStatus.innerText = 'Cache Installed';
          btn.innerText = '⚡ Re-Compile';
        }
      } else {
        pStatus.className = 'status-pill status-compiling';
        pStatus.innerText = 'Compiling...';
        btn.disabled = true;
      }
    }

    function updateProgress(p) {
      recentProgress[p.gameId] = p;
      const card = document.getElementById(`game-${p.gameId}`);
      if (!card) return;
      applyProgressToCard(card, p);
    }

    function getEngineName(code) {
      const engines = ['Unknown', 'UnrealEngine4', 'UnrealEngine5', 'Unity', 'RedEngine', 'FromSoftware', 'ReEngine', 'Northlight', 'DirectX12', 'DirectX11', 'Dagor'];
      return typeof code === 'number' ? (engines[code] || 'DirectX') : code;
    }

    function renderGames() {
      const grid = document.getElementById('game-grid');
      if (!currentGames || currentGames.length === 0) {
        updateFilterBarUI(0);
        grid.innerHTML = '<div style="color: var(--text-muted); padding: 20px;">No games detected in Steam, Heroic, or Lutris libraries. Click <strong>🩺 Diagnose</strong> above to inspect library paths.</div>';
        return;
      }

      const filteredGames = getFilteredGames();
      updateFilterBarUI(filteredGames.length);

      if (filteredGames.length === 0) {
        grid.innerHTML = `
          <div style="grid-column: 1 / -1; background: var(--card-bg); border: 1px dashed var(--card-border); border-radius: 10px; padding: 28px; display: flex; flex-direction: column; align-items: center; gap: 12px; color: var(--text-muted);">
            <div>No installed games match your current filter${searchQuery ? ` ("<strong>${escapeHtml(searchQuery)}</strong>")` : ''}.</div>
            <button class="btn-secondary" style="font-size: 12px; padding: 6px 14px;" onclick="resetAllFilters()">✕ Reset Filters</button>
          </div>
        `;
        return;
      }

      grid.innerHTML = filteredGames.map(g => {
        const isCached = g.state === 1 || g.state === 'Cached';
        const isStale = g.state === 2 || g.state === 'Outdated';
        const statusClass = isCached ? 'status-cached' : (isStale ? 'status-stale' : 'status-uncompiled');
        const statusLabel = isCached
          ? (g.newRecordedPsos > 0 ? `Cached (+${g.newRecordedPsos.toLocaleString()} new)` : 'Cached (Ready)')
          : (isStale
              ? (g.staleReason === 'cache truncated'
                  ? 'Stale (>2 GB Truncated)'
                  : (g.staleReason ? `Stale (${g.staleReason})` : 'Stale'))
              : 'Needs Pre-Compilation');
        const statusTitle = g.staleReason === 'cache truncated'
          ? 'Legacy single-file NVIDIA cache hit NVIDIA\'s ~2 GB limit. Click Re-Compile once to split into multi-part cache files.'
          : '';
        const sourceBadge = g.source === 0 || g.source === 'Steam' ? 'badge-steam' : (g.source === 1 || g.source === 'Heroic' ? 'badge-heroic' : 'badge-lutris');
        const baseSourceName = g.source === 0 || g.source === 'Steam' ? 'Steam' : (g.source === 1 || g.source === 'Heroic' ? 'Heroic' : 'Lutris');
        const sourceName = g.isFlatpak ? `${baseSourceName} (Flatpak)` : baseSourceName;
        const engineLabel = getEngineName(g.engine);
        const cacheSizeLabel = formatBytes(g.cacheSizeBytes);
        const newRecBadge = g.newRecordedPsos > 0 ? ` <span style="color: #d29922;">(+${g.newRecordedPsos.toLocaleString()} new)</span>` : '';

        return `
          <div class="game-card" id="game-${g.id}">
            <div class="card-top">
              <div>
                <div class="game-title">${g.name}</div>
                <div class="badges">
                  <span class="badge ${sourceBadge}">${sourceName}</span>
                  <span class="badge badge-engine">${engineLabel}</span>
                </div>
              </div>
              <div class="status-pill ${statusClass}"${statusTitle ? ` title="${escapeHtml(statusTitle)}"` : ''}>${statusLabel}</div>
            </div>

            <div class="meta-row">
              <span>Estimated Shaders:</span>
              <strong>~${(g.totalShadersEstimated || 5000).toLocaleString()}</strong>
            </div>

            <div class="meta-row">
              <span>On-Disk Shader Cache:</span>
              <strong style="${g.cacheSizeBytes > 0 ? 'color: #3fb950;' : ''}">${cacheSizeLabel}</strong>
            </div>

            <div class="meta-row">
              <span>Recorded Pipelines:</span>
              <strong style="${g.recordedPsos > 0 ? 'color: #bc8cff;' : ''}">${(g.recordedPsos || 0).toLocaleString()}${newRecBadge}${g.recorderInstalled ? ' • recording' : ''}</strong>
            </div>

            <div class="progress-box">
              <div class="progress-track"><div class="progress-fill"></div></div>
              <div class="progress-text"><span>Initializing...</span><span>0%</span></div>
            </div>

            <div class="card-actions" style="display: flex; gap: 8px; justify-content: flex-end;">
              ${g.cacheSizeBytes > 0 ? `
              <button class="btn-danger" style="padding: 7px 12px; font-size: 12px;" onclick="clearGameCache('${g.id}', '${g.name.replace(/'/g, "\\'")}')" title="Delete compiled shader cache on disk for this game">
                🗑️ Clear Cache
              </button>` : ''}
              <button class="btn-secondary" style="padding: 7px 12px; font-size: 12px; ${g.recorderInstalled ? 'border-color: #bc8cff; color: #bc8cff;' : ''}" onclick="toggleRecorder('${g.id}')" title="Record mode: a d3d12.dll next to the game records every pipeline it creates while you play, so the next compile covers exactly what the game uses">
                ${g.recorderInstalled ? '⏺ Recording (on)' : '⏺ Record'}
              </button>
              <button class="btn-primary" onclick="compileGame('${g.id}')">
                ${(isCached || isStale) ? '⚡ Re-Compile' : '⚡ Kill Stutter'}
              </button>
            </div>
          </div>
        `;
      }).join('');

      for (const id of Object.keys(recentProgress)) {
        const card = document.getElementById(`game-${id}`);
        if (card) applyProgressToCard(card, recentProgress[id]);
      }
    }

    // Set up bidirectional bridge
    let bridgeReady = false;
    let stateReceived = false;
    const origUpdateState = updateState;
    updateState = function(state) {
      stateReceived = true;
      origUpdateState(state);
    };
    function setupBridge() {
      if (window.external && window.external.receiveMessage) {
        window.external.receiveMessage(onMessage);
        bridgeReady = true;
        scanGames();
      } else {
        setTimeout(setupBridge, 30);
      }
    }
    setupBridge();

    // Safety fallback: if state hasn't arrived yet, request it again
    const retryInterval = setInterval(() => {
      if (!stateReceived) {
        scanGames();
      } else {
        clearInterval(retryInterval);
      }
    }, 1500);
  </script>
</body>
</html>
""";
}
