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
      --bg: #0d1117;
      --card-bg: #161b22;
      --card-border: #30363d;
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
    .header-actions { display: flex; gap: 10px; }
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
      margin-bottom: 16px;
      display: flex;
      justify-content: space-between;
      align-items: center;
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
      <button class="btn-primary" onclick="compileAll()">⚡ Pre-Compile All</button>
      <button class="btn-danger" onclick="clearCache()" title="Purge ALL compiled shaders from GPU driver and Steam game caches">🗑️ Clear All Caches</button>
    </div>
  </header>

  <main>
    <div class="section-title">
      <span id="library-count">Installed Games</span>
      <span style="font-size: 11px; color: var(--text-muted);">Shaders are compiled directly into GPU driver cache ahead of time</span>
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

        <div style="font-size: 11px; color: var(--text-muted); padding: 4px 2px;">
          Saved in <code>~/.local/share/shaderless/settings.json</code>
        </div>
      </div>
    </div>
  </div>

  <script>
    let currentGames = [];
    let currentSettings = { autoRecordAllGames: false, autoCompileOnExit: false };

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
      document.getElementById('chk-auto-record').checked = !!currentSettings.autoRecordAllGames;
      document.getElementById('chk-auto-compile').checked = !!currentSettings.autoCompileOnExit;
      document.getElementById('settings-modal').style.display = 'flex';
    }

    function closeSettings() {
      document.getElementById('settings-modal').style.display = 'none';
    }

    function saveSettings() {
      const autoRecord = document.getElementById('chk-auto-record').checked;
      const autoCompile = document.getElementById('chk-auto-compile').checked;
      currentSettings = { autoRecordAllGames: autoRecord, autoCompileOnExit: autoCompile };
      post({
        action: 'save-settings',
        autoRecordAllGames: autoRecord,
        autoCompileOnExit: autoCompile
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
        document.getElementById('library-count').innerText = `Installed Games (${currentGames.length})`;
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
      const engines = ['Unknown', 'UnrealEngine4', 'UnrealEngine5', 'Unity', 'RedEngine', 'FromSoftware', 'ReEngine', 'Northlight', 'DirectX12', 'DirectX11'];
      return typeof code === 'number' ? (engines[code] || 'DirectX') : code;
    }

    function renderGames() {
      const grid = document.getElementById('game-grid');
      if (!currentGames || currentGames.length === 0) {
        grid.innerHTML = '<div style="color: var(--text-muted); padding: 20px;">No games detected in Steam, Heroic, or Lutris libraries. Click <strong>🩺 Diagnose</strong> above to inspect library paths.</div>';
        return;
      }

      grid.innerHTML = currentGames.map(g => {
        const isCached = g.state === 1 || g.state === 'Cached';
        const isStale = g.state === 2 || g.state === 'Outdated';
        const statusClass = isCached ? 'status-cached' : (isStale ? 'status-stale' : 'status-uncompiled');
        const statusLabel = isCached
          ? (g.newRecordedPsos > 0 ? `Cached (+${g.newRecordedPsos.toLocaleString()} new)` : 'Cached (Ready)')
          : (isStale ? (g.staleReason ? `Stale (${g.staleReason})` : 'Stale') : 'Needs Pre-Compilation');
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
              <div class="status-pill ${statusClass}">${statusLabel}</div>
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
