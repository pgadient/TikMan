namespace TikMan.Web;

/// <summary>Static web assets, embedded as strings so they ship inside the single-file exe (there is no
/// wwwroot folder on disk to serve from). Increment 1 is a live, auto-refreshing device list; scan
/// control, actions, topology, terminal and VNC follow in later increments.</summary>
internal static class WebAssets
{
    public const string IndexHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>TikMan Web</title>
<style>
  :root { color-scheme: light dark; --bg:#f6f7f9; --fg:#1b1d21; --muted:#767b85; --card:#ffffff;
          --line:#e4e6ea; --accent:#2266cc; --gw:#f68500; }
  @media (prefers-color-scheme: dark) {
    :root { --bg:#16181d; --fg:#e7e9ee; --muted:#8b919c; --card:#1e2128; --line:#2b2f38; }
  }
  * { box-sizing: border-box; }
  body { margin:0; font:14px/1.45 "Segoe UI",system-ui,sans-serif; background:var(--bg); color:var(--fg); }
  header { display:flex; align-items:baseline; gap:12px; padding:14px 18px; border-bottom:1px solid var(--line);
           position:sticky; top:0; background:var(--card); flex-wrap:wrap; }
  header h1 { font-size:18px; margin:0; font-weight:600; }
  header .ver { color:var(--muted); font-size:12px; }
  header .count { margin-left:auto; color:var(--muted); font-size:13px; }
  .bar { padding:10px 18px; display:flex; gap:10px; align-items:center; flex-wrap:wrap; }
  input[type=search] { flex:1; min-width:160px; padding:7px 10px; border:1px solid var(--line);
           border-radius:7px; background:var(--card); color:var(--fg); font-size:14px; }
  button { padding:7px 14px; border:none; border-radius:7px; background:var(--accent); color:#fff;
           font-size:14px; font-weight:600; cursor:pointer; }
  button:disabled { opacity:.5; cursor:default; }
  .prog { padding:0 18px 8px; align-items:center; gap:10px; }
  .prog:not([hidden]) { display:flex; } /* keep [hidden] able to hide it – a bare display:flex would win over [hidden] */
  .track { flex:1; height:8px; border-radius:6px; background:var(--line); overflow:hidden; }
  .fill { height:100%; width:0; background:var(--gw); border-radius:6px; transition:width .3s ease; }
  .fill.indet { width:35% !important; animation:slide 1.1s ease-in-out infinite; }
  @keyframes slide { 0%{margin-left:-35%} 100%{margin-left:100%} }
  .pphase { color:var(--muted); font-size:12px; white-space:nowrap; }
  tr.row { cursor:pointer; }
  tr.row:hover td { background:color-mix(in srgb, var(--accent) 8%, transparent); }
  .modal { position:fixed; inset:0; background:rgba(0,0,0,.45); align-items:center; justify-content:center;
           padding:16px; z-index:10; }
  .modal:not([hidden]) { display:flex; }
  .sheet { background:var(--card); border-radius:12px; max-width:560px; width:100%; max-height:85vh;
           overflow:auto; box-shadow:0 8px 40px rgba(0,0,0,.35); }
  .mhead { display:flex; align-items:center; gap:10px; padding:14px 18px; border-bottom:1px solid var(--line);
           position:sticky; top:0; background:var(--card); }
  .mhead h2 { margin:0; font-size:17px; font-weight:600; flex:1; }
  .x { background:transparent; color:var(--muted); padding:4px 9px; font-weight:400; }
  #mbody { padding:4px 18px; }
  .kv { display:grid; grid-template-columns:130px 1fr; gap:2px 12px; padding:7px 0;
        border-bottom:1px solid var(--line); font-size:13px; }
  .kv:last-child { border-bottom:none; }
  .kv b { color:var(--muted); font-weight:600; }
  .kv span { word-break:break-word; }
  .mfoot { padding:14px 18px; display:flex; align-items:center; gap:12px; border-top:1px solid var(--line);
           position:sticky; bottom:0; background:var(--card); }
  .mfoot button { background:var(--gw); }
  .mlogin { padding:10px 18px 4px; border-top:1px solid var(--line); }
  .mlogin h3 { margin:0 0 8px; font-size:12px; color:var(--muted); font-weight:600;
               text-transform:uppercase; letter-spacing:.03em; }
  #mloginform { display:flex; gap:8px; flex-wrap:wrap; }
  #mloginform input { flex:1; min-width:120px; padding:7px 10px; border:1px solid var(--line);
                      border-radius:7px; background:var(--card); color:var(--fg); font-size:14px; }
  .mbackup { padding:10px 18px 4px; border-top:1px solid var(--line); }
  .mbackup h3 { margin:0 0 8px; font-size:12px; color:var(--muted); font-weight:600;
                text-transform:uppercase; letter-spacing:.03em; }
  #mbackupbtns { display:flex; gap:8px; flex-wrap:wrap; }
  .wrap { overflow-x:auto; padding:0 18px 24px; }
  table { border-collapse:collapse; width:100%; min-width:640px; background:var(--card); border-radius:10px;
          overflow:hidden; box-shadow:0 1px 2px rgba(0,0,0,.06); }
  th,td { text-align:left; padding:9px 12px; border-bottom:1px solid var(--line); white-space:nowrap; }
  th { font-size:12px; text-transform:uppercase; letter-spacing:.03em; color:var(--muted); font-weight:600;
       position:sticky; top:0; background:var(--card); cursor:pointer; user-select:none; }
  tr:last-child td { border-bottom:none; }
  td.name { font-weight:600; }
  .tag { display:inline-block; padding:1px 7px; border-radius:20px; font-size:11px; font-weight:600;
         background:color-mix(in srgb, var(--accent) 16%, transparent); color:var(--accent); }
  .gw { background:color-mix(in srgb, var(--gw) 20%, transparent); color:var(--gw); }
  .lock { color:var(--muted); font-size:12px; }
  /* Service chips, same idea as the app's row badges. Clickable ones keep the link colour. */
  .badge { display:inline-block; padding:1px 6px; border-radius:4px; font-size:11px; font-weight:600;
           background:color-mix(in srgb, var(--fg) 12%, transparent); color:var(--fg);
           text-decoration:none; white-space:nowrap; }
  /* ⚠️ Hover must not repaint a coloured chip: the colour IS the service, and swapping it on hover made a
     clickable https badge look like a different protocol under the cursor. Only brightness changes. */
  a.badge:hover, .badge.act:hover { filter:brightness(1.15); }
  .badge.act { cursor:pointer; }
  a.badge:not([style]):hover { background:color-mix(in srgb, var(--accent) 26%, transparent); color:var(--accent); }
  .muted { color:var(--muted); }
  .empty { padding:40px; text-align:center; color:var(--muted); }
  .tabs { display:flex; gap:4px; padding:8px 18px 0; }
  .tab { background:transparent; color:var(--muted); border:none; border-bottom:2px solid transparent;
         border-radius:0; padding:7px 12px; font-weight:600; cursor:pointer; }
  .tab.on { color:var(--fg); border-bottom-color:var(--accent); }
  .mapbar { display:flex; gap:6px; align-items:center; padding:10px 18px; flex-wrap:wrap; }
  .seg { background:var(--card); color:var(--fg); border:1px solid var(--line); padding:6px 12px; font-size:13px; }
  .seg.on { background:var(--accent); color:#fff; border-color:var(--accent); }
  #mapRefresh { background:var(--card); color:var(--fg); border:1px solid var(--line); padding:6px 11px; }
  #mapsvg { display:block; width:100%; height:calc(100vh - 190px); background:var(--bg);
            touch-action:none; user-select:none; cursor:grab; }
  #mapsvg.grabbing { cursor:grabbing; }
  #mapsvg text { pointer-events:none; }
  #mapsvg .node { cursor:pointer; }
  #term { position:fixed; inset:0; z-index:20; background:#000; flex-direction:column; }
  #term:not([hidden]) { display:flex; }
  .termhead { display:flex; align-items:center; gap:10px; padding:8px 14px; background:#111; color:#ddd; }
  .termhead span { flex:1; font-size:13px; font-weight:600; }
  .termhead .x { color:#ddd; }
  #termbox { flex:1; min-height:0; padding:6px 8px; overflow:hidden; }
  #vnc { position:fixed; inset:0; z-index:20; background:#000; flex-direction:column; }
  #vnc:not([hidden]) { display:flex; }
  #vncbox { flex:1; min-height:0; display:flex; align-items:center; justify-content:center; }
  /* Connection-lost bar: sticky at the very top, unmissable red, above the VNC/terminal overlays too. */
  #lostbar { position:sticky; top:0; z-index:40; background:#c62828; color:#fff; font-weight:600;
             text-align:center; padding:9px 14px; box-shadow:0 2px 6px rgba(0,0,0,.35); }
  #lostbar[hidden] { display:none; }
</style>
</head>
<body>
<!-- Full-width red bar shown when the browser can no longer reach TikMan (the app was closed, the machine
     slept, the network dropped). It sits above everything and is sticky, so a lost connection is impossible
     to miss – the polled data would otherwise just quietly freeze at its last values. -->
<div id="lostbar" hidden>Connection to TikMan lost — trying to reconnect…</div>
<header>
  <!-- "Web" is part of the product name here, not a separate line: this IS TikMan, served over HTTP.
       "(live)" rides at the version's size so the title stays the title. The "tap a row for details" hint
       is gone: the rows are obviously clickable once you try one, and it was on screen permanently for a
       thing you learn once. -->
  <h1 id="title">TikMan Web</h1><span class="ver" id="ver"></span>
  <span class="ver">(live)</span>
  <span class="count" id="count"></span>
</header>
<div class="tabs">
  <!-- Same four views as the desktop app, and named the same – "Devices"/"Map" made the browser look
       like a different product with a different feature set. -->
  <button id="tabIpv4" class="tab on">IPv4</button>
  <button id="tabIpv6" class="tab">IPv6</button>
  <button id="tabDist" class="tab">IP distribution</button>
  <button id="tabTopo" class="tab">Topology</button>
</div>
<div id="devicesView">
<div class="bar">
  <button id="scan">⟳ Scan</button>
  <input type="search" id="filter" placeholder="Filter… (name, IP, MAC, vendor, type, protocols)" autocomplete="off">
  <span class="muted" id="status"></span>
</div>
<div class="prog" id="prog" hidden>
  <div class="track"><div class="fill" id="pfill"></div></div>
  <span class="pphase" id="pphase"></span>
</div>
<div class="wrap">
  <table>
    <thead><tr>
      <th data-k="type">Type</th><th data-k="name">Name</th><th data-k="ip">IPv4</th>
      <th data-k="ipv6Summary">IPv6</th><th data-k="source">Source</th><th>Supported protocols</th>
      <th data-k="mac">MAC</th><th data-k="vendor">Vendor</th><th data-k="macVendor">MAC vendor</th>
      <th data-k="model">Model</th><th data-k="serial">Serial</th><th data-k="os">OS</th>
      <th data-k="firmware">Firmware</th><th data-k="latestVersion">Latest</th>
      <th data-k="cpu">CPU</th><th data-k="memory">RAM</th><th data-k="uptime">Uptime</th>
      <th data-k="status">Status</th>
    </tr></thead>
    <tbody id="rows"></tbody>
  </table>
  <div class="empty" id="empty" hidden>No devices yet — start a scan in the desktop app.</div>
</div>
</div>
<div id="ipv6View" hidden>
<div class="wrap">
  <table>
    <thead><tr>
      <th data-k="group">#</th><th data-k="type">Type</th><th data-k="name">Name</th>
      <th data-k="address">IPv6</th><th data-k="scope">Scope</th>
      <th>Supported protocols</th>
      <th data-k="ip">IPv4</th><th data-k="mac">MAC</th>
      <th data-k="macVendor">MAC vendor</th><th data-k="vendor">Vendor</th>
      <th data-k="model">Model</th><th data-k="serial">Serial</th><th data-k="os">OS</th>
      <th data-k="shares">Shares</th>
      <th data-k="firmware">Firmware</th><th data-k="latestVersion">Latest</th>
      <th data-k="cpu">CPU</th><th data-k="memory">RAM</th><th data-k="uptime">Uptime</th>
      <th data-k="status">Status</th>
    </tr></thead>
    <tbody id="v6rows"></tbody>
  </table>
  <div class="empty" id="v6empty" hidden>No IPv6 addresses found yet.</div>
</div>
</div>
<div id="mapView" hidden>
  <div class="mapbar">
    <button id="mapRefresh">⟳</button>
    <span class="muted" id="mapstatus"></span>
  </div>
  <svg id="mapsvg" xmlns="http://www.w3.org/2000/svg"></svg>
</div>
<div class="modal" id="modal" hidden>
  <div class="sheet">
    <div class="mhead"><h2 id="mname"></h2><button class="x" id="mclose">✕</button></div>
    <div id="mbody"></div>
    <div class="mlogin">
      <h3>Login</h3>
      <div class="muted" id="mloginhttp" hidden>🔒 HTTPS required to set a login — enable it in the desktop app's settings.</div>
      <div id="mloginform">
        <input id="mluser" placeholder="User" autocomplete="off">
        <input id="mlpass" type="password" placeholder="Password" autocomplete="new-password">
        <button id="mlsave">Save login</button>
      </div>
    </div>
    <div class="mbackup" id="mbackup" hidden>
      <h3>Backup</h3>
      <div id="mbackupbtns">
        <button id="mbrsc">Config (.rsc)</button>
        <button id="mbfull">Full (.backup)</button>
      </div>
    </div>
    <!-- ⚠️ No Terminal / VNC buttons here. Those are reached by clicking the device's ssh / vnc chip in
         the list – the same gesture as in the desktop client. Having them in the detail panel as well was
         a second, differently-gated way to do one thing, and it did not match the app. -->
    <div class="mfoot">
      <button id="mwake" hidden>⏻ Wake</button>
      <span class="muted" id="mtoast"></span>
    </div>
    <!-- Over plain HTTP the backup buttons are hidden (a config can hold secrets, so the server refuses it
         without TLS). Without this line they would just be absent, which reads as "this build cannot do
         backups" rather than "turn on HTTPS". -->
    <div class="muted" id="msecurehint" hidden>🔒 Backup and setting a login need HTTPS — enable it in the desktop app's web-server settings.</div>
  </div>
</div>
<div id="term" hidden>
  <div class="termhead"><span id="termtitle">SSH</span><button class="x" id="termclose">✕</button></div>
  <div id="termbox"></div>
</div>
<div id="vnc" hidden>
  <div class="termhead"><span id="vnctitle">VNC</span><button class="x" id="vncclose">✕</button></div>
  <div id="vncbox"></div>
</div>
<!-- No footer. It repeated the header word for word at the bottom of every page – the product name and
     "live" are already up there, and a second copy is just a line that scrolls with the table. -->
<script>
let devices = [], v6rows = [], sortKey = "ip", sortDir = 1;
let mapPhysical = false, mapLoaded = false, vb = { x:0, y:0, w:100, h:100 };
const secure = location.protocol === "https:";
const $ = s => document.querySelector(s);

async function j(url){ const r = await fetch(url,{cache:"no-store"}); if(!r.ok) throw new Error(r.status); return r.json(); }

async function loadInfo(){
  // The version comes from the running app (/api/info), never from a constant in this page – a hard-coded
  // one silently claimed v1.0.0 long after the app had moved on.
  try { const i = await j("/api/info");
        $("#title").textContent = (i.title || "TikMan") + " Web";
        $("#ver").textContent = i.version ? "v"+i.version : "";
        document.title = (i.title||"TikMan") + " Web"; } catch{}
}

// A row's service badges, same idea as the app's coloured protocol chips.
// Service chips in the SAME colours as the app: green = encrypted, orange = plain web, blue = shell,
// red = cleartext login, teal = file sharing, and so on.
// ⚠️ The colour comes from the payload (BadgeDto.Colour), never from a lookup table repeated here – the
// meaning is carried by the colour, so the browser and the app disagreeing about it is worse than no
// colour at all. A badge without one falls back to the neutral chip.
function badges(list, id){
  if(!list || !list.length) return '<span class="muted">no answer</span>';
  return list.map(b => {
    // White text on the saturated fills, which is what the app uses and what these hues are picked for.
    const style = b.colour ? ` style="background:${esc(b.colour)};color:#fff"` : "";
    const tip = b.tooltip || b.url || b.name;
    const scheme = (b.url||"").split(":")[0].toLowerCase();
    // ⚠️ ssh:// and vnc:// are NOT rendered as links. Followed as an href the browser hands them to a
    // client on the machine running the browser – never what you want from a remote dashboard.
    // Clicking one opens the in-browser terminal / VNC instead, which is the same gesture as in the
    // desktop client (there the ssh badge opens the terminal too). No stored login is needed: the
    // terminal asks for the credentials itself, the way any SSH client does.
    if(scheme==="ssh" || scheme==="vnc"){
      if(!secure)   // the relay is a wss socket; the server refuses it over plain HTTP
        return `<span class="badge" title="${esc(tip)} — needs HTTPS"${style}>${esc(b.name)}</span>`;
      const act = scheme==="ssh" ? "term" : "vnc";
      return `<span class="badge act" data-act="${act}" data-id="${esc(id)}" title="${esc(tip)}"${style}>${esc(b.name)}</span>`;
    }
    return b.url
      ? `<a class="badge" href="${esc(b.url)}" target="_blank" rel="noopener" title="${esc(tip)}"${style}>${esc(b.name)}</a>`
      : `<span class="badge" title="${esc(tip)}"${style}>${esc(b.name)}</span>`;
  }).join(" ");
}

// A click on an ssh/vnc chip opens the relayed terminal / VNC, and swallows the event so the row's
// "open detail" handler underneath does not also fire. Returns true when it handled the click.
function badgeAct(e){
  const a = e.target.closest(".badge.act");
  if(!a) return false;
  e.stopPropagation();
  if(a.dataset.act==="term") openTerminal(a.dataset.id); else openVnc(a.dataset.id);
  return true;
}

function ipKey(ip){ const m=(ip||"").match(/(\d+)\.(\d+)\.(\d+)\.(\d+)/);
  return m ? ((+m[1]<<24)>>>0)+(+m[2]<<16)+(+m[3]<<8)+(+m[4]) : 1e12 + (ip||"").localeCompare(""); }

function render(){
  const f = $("#filter").value.trim().toLowerCase();
  let list = devices.filter(d => !f ||
    [d.name,d.ip,d.mac,d.vendor,d.macVendor,d.type,d.model,d.status,d.serial,d.os,d.firmware,d.ipv6Summary]
      .some(v => (v||"").toLowerCase().includes(f)) ||
    (d.badges||[]).some(b => (b.name||"").toLowerCase().includes(f)));
  list.sort((a,b)=>{
    let x,y;
    if(sortKey==="ip"){ x=ipKey(a.ip); y=ipKey(b.ip); }
    else { x=(a[sortKey]||"").toLowerCase(); y=(b[sortKey]||"").toLowerCase(); }
    return (x<y?-1:x>y?1:0)*sortDir;
  });
  $("#count").textContent = devices.length + " devices" + (f? " · "+list.length+" shown":"");
  $("#empty").hidden = devices.length>0;
  $("#rows").innerHTML = list.map(d => `<tr class="row" data-id="${esc(d.id)}">
    <td>${d.type?`<span class="tag ${d.isGateway?'gw':''}">${esc(d.type)}</span>`:''}</td>
    <td class="name">${esc(d.name)||'<span class="muted">—</span>'} ${d.hasLogin?'<span class="lock" title="has login">🔑</span>':''}</td>
    <td>${esc(d.ip)}</td>
    <td class="muted">${v6short(d.ipv6Summary)}</td>
    <td class="muted">${esc(d.source||"")}</td>
    <td>${badges(d.badges, d.id)}</td>
    <td class="muted">${esc(d.mac)}</td>
    <td>${esc(d.vendor)}</td><td class="muted">${esc(d.macVendor)}</td>
    <td>${esc(d.model)}</td><td class="muted">${esc(d.serial)}</td>
    <td>${esc(d.os)}</td><td>${esc(d.firmware)}</td>
    <td>${d.updateAvailable?`<b>${esc(d.latestVersion)}</b>`:esc(d.latestVersion)}</td>
    <td>${esc(d.cpu)}</td><td>${esc(d.memory)}</td><td>${esc(d.uptime)}</td>
    <td>${esc(d.status)}</td></tr>`).join("");
}

// The IPv4 table shows the FIRST v6 address and ", …" for the rest.
// A dual-stack host routinely carries four or five at once (global, ULA, link-local, one or two privacy
// ones), and the full list made that one column wider than every other column in the table put together,
// pushing the useful ones off screen. The whole list is on hover, in the detail panel, and one tab away in
// the IPv6 view – where each address has its own row, which is what that view is for.
// ⚠️ Display only: the filter above still searches the complete summary, so typing any address still finds
// its device even when the cell is not showing it.
function v6short(summary){
  const all = (summary||"").split(/\s+/).filter(Boolean);
  if(!all.length) return "";
  const shown = all.length>1 ? esc(all[0])+", …" : esc(all[0]);
  return `<span title="${esc(all.join("\n"))}">${shown}</span>`;
}

// The IPv6 view: one row per address, so two addresses of the same device can be compared side by side.
function renderIpv6(){
  const f = $("#filter").value.trim().toLowerCase();
  const list = v6rows.filter(r => !f ||
    [r.name,r.address,r.scope,r.ip,r.mac,r.vendor,r.macVendor,r.model,r.type,r.status,
     r.serial,r.os,r.shares,r.firmware,r.latestVersion,r.cpu,r.memory,r.uptime]
      .some(v => (v||"").toLowerCase().includes(f)) ||
    (r.badges||[]).some(b => (b.name||"").toLowerCase().includes(f)));
  $("#v6empty").hidden = v6rows.length>0;
  $("#v6rows").innerHTML = list.map(r => `<tr class="row" data-id="${esc(r.id)}">
    <td class="muted">${r.group}</td>
    <td>${r.type?`<span class="tag">${esc(r.type)}</span>`:''}</td>
    <td class="name">${esc(r.name)||'<span class="muted">—</span>'}</td>
    <td>${esc(r.address)}</td><td class="muted">${esc(r.scope)}</td>
    <td>${badges(r.badges, r.id)}</td>
    <td>${esc(r.ip)}</td><td class="muted">${esc(r.mac)}</td>
    <td class="muted">${esc(r.macVendor)}</td><td>${esc(r.vendor)}</td>
    <td>${esc(r.model)}</td><td class="muted">${esc(r.serial)}</td><td>${esc(r.os)}</td>
    <td class="muted">${esc(r.shares)}</td>
    <td>${esc(r.firmware)}</td><td>${esc(r.latestVersion)}</td>
    <td class="muted">${esc(r.cpu)}</td><td class="muted">${esc(r.memory)}</td>
    <td class="muted">${esc(r.uptime)}</td>
    <td>${esc(r.status)}</td></tr>`).join("");
}
function esc(s){ return (s||"").replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])); }

// One place decides whether TikMan is reachable, so the red bar can't disagree with itself between the two
// pollers. Any successful fetch clears it; any failed one raises it.
function setConnected(ok){
  const bar = $("#lostbar");
  if(bar) bar.hidden = ok;
  if(ok) $("#status").textContent = "";
}

async function tick(){
  try { devices = await j("/api/devices"); setConnected(true); render(); }
  catch(e){ setConnected(false); }
}

let wasScanning = false;
async function pollStatus(){
  try {
    const s = await j("/api/status");
    const prog = $("#prog"), fill = $("#pfill");
    if(s.scanning){
      prog.hidden = false; $("#scan").disabled = true;
      if(s.progress < 0){ fill.classList.add("indet"); }
      else { fill.classList.remove("indet"); fill.style.width = Math.round(s.progress*100)+"%"; }
      $("#pphase").textContent = (s.phase||"Scanning") + (s.progress>=0 ? " · "+Math.round(s.progress*100)+"%" : "");
      tick(); // devices appear live during the scan
    } else {
      prog.hidden = true; $("#scan").disabled = false;
      if(wasScanning) tick(); // one final refresh when a scan just finished
    }
    wasScanning = s.scanning;
    setConnected(true);
  } catch(e){ setConnected(false); }
}
async function scanNow(){ try { await fetch("/api/scan",{method:"POST"}); pollStatus(); } catch{} }

async function openDetail(id){
  try {
    const d = await j("/api/device?id="+encodeURIComponent(id));
    $("#mname").textContent = d.name || d.ip || "Device";
    const rows = [["IP",d.ip],["MAC",d.mac],["Type",d.type],["Vendor",d.vendor],
                  ["Model",d.model],["Status",d.status],["Login",d.hasLogin?"yes":"no"]];
    if(d.ipv6 && d.ipv6.length) rows.push(["IPv6", d.ipv6.join("\n")]);
    (d.info||[]).forEach(kv=>rows.push([kv.key, kv.value]));
    $("#mbody").innerHTML = rows.filter(r=>r[1]!=null && r[1]!=="").map(r=>
      `<div class="kv"><b>${esc(r[0])}</b><span>${esc(r[1]).replace(/\n/g,"<br>")}</span></div>`).join("");
    const w=$("#mwake"); w.hidden=!d.canWake; w.dataset.id=d.id;
    $("#modal").dataset.id = d.id;
    $("#mluser").value = d.user || ""; $("#mlpass").value = "";
    $("#mloginhttp").hidden = secure; $("#mloginform").style.display = secure ? "flex" : "none";
    $("#mbackup").hidden = !(secure && d.hasLogin);
    // Explain the gap over HTTP, but only where there is actually something behind it.
    $("#msecurehint").hidden = secure || !d.hasLogin;
    $("#mtoast").textContent=""; $("#modal").hidden=false;
  } catch(e){}
}
async function saveLogin(id){
  if(!id) return;
  $("#mtoast").textContent="…";
  const body = new URLSearchParams({ id, user:$("#mluser").value, password:$("#mlpass").value });
  try {
    const r = await (await fetch("/api/login",{method:"POST",
      headers:{"Content-Type":"application/x-www-form-urlencoded"}, body})).json();
    $("#mtoast").textContent = r.message || (r.ok?"saved":"failed");
    if(r.ok){ $("#mlpass").value=""; tick(); }
  } catch { $("#mtoast").textContent="failed"; }
}
async function backup(id, full){
  if(!id) return;
  $("#mtoast").textContent = full ? "creating full backup…" : "creating config backup…";
  try {
    const r = await fetch(`/api/backup?id=${encodeURIComponent(id)}&full=${full?"true":"false"}`, {method:"POST"});
    if(!r.ok){ let m="backup failed"; try{ m=(await r.json()).message||m; }catch{} $("#mtoast").textContent=m; return; }
    const blob = await r.blob();
    const cd = r.headers.get("Content-Disposition")||"";
    const mm = cd.match(/filename="?([^"]+)"?/);
    const name = mm ? mm[1] : (full?"backup.backup":"config.rsc");
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a"); a.href=url; a.download=name;
    document.body.appendChild(a); a.click(); a.remove(); URL.revokeObjectURL(url);
    $("#mtoast").textContent = "downloaded "+name;
  } catch { $("#mtoast").textContent="backup failed"; }
}
async function wake(id){
  $("#mtoast").textContent="…";
  try { const r = await (await fetch("/api/wake?id="+encodeURIComponent(id),{method:"POST"})).json();
        $("#mtoast").textContent = r.message || (r.ok?"sent":"failed"); }
  catch { $("#mtoast").textContent="failed"; }
}

// The display name of a device from the loaded list ("" when it has none / is not loaded).
function deviceName(id){
  const d = devices.find(x => x.id === id);
  return d ? (d.name || d.ip || "") : "";
}

// ---- SSH terminal (xterm.js, lazy-loaded) ----
let xtermReady = null, currentTerm = null;
function loadXterm(){
  if(xtermReady) return xtermReady;
  xtermReady = new Promise((resolve,reject)=>{
    const css=document.createElement("link"); css.rel="stylesheet"; css.href="/xterm.css"; document.head.appendChild(css);
    const s1=document.createElement("script"); s1.src="/xterm.js";
    s1.onload=()=>{ const s2=document.createElement("script"); s2.src="/xterm-addon-fit.js"; s2.onload=resolve; s2.onerror=reject; document.head.appendChild(s2); };
    s1.onerror=reject; document.head.appendChild(s1);
  });
  return xtermReady;
}
async function openTerminal(id){
  if(!id || !secure) return;
  try { await loadXterm(); } catch { $("#mtoast").textContent="failed to load terminal"; return; }
  $("#modal").hidden=true;
  // ⚠️ The name comes from the device list, not from the modal's heading: the terminal is opened straight
  // from a chip in the row now, so the modal may never have been opened and its heading would name
  // whichever device was looked at last.
  $("#termtitle").textContent = "SSH · " + (deviceName(id) || id);
  $("#term").hidden=false;
  const term = new Terminal({ fontSize:13, cursorBlink:true, theme:{ background:"#000000" } });
  const fit = new FitAddon.FitAddon(); term.loadAddon(fit);
  term.open($("#termbox")); fit.fit(); term.focus();
  const ws = new WebSocket(`wss://${location.host}/ws/ssh?id=${encodeURIComponent(id)}&cols=${term.cols}&rows=${term.rows}`);
  ws.binaryType="arraybuffer";
  const enc=new TextEncoder();
  ws.onmessage = e => term.write(typeof e.data==="string" ? e.data : new Uint8Array(e.data));
  ws.onclose = ()=>{ try{ term.write("\r\n\x1b[90m[disconnected]\x1b[0m\r\n"); }catch{} };
  term.onData(d=>{ if(ws.readyState===1) ws.send(enc.encode(d)); });
  const onResize=()=>{ try{ fit.fit(); }catch{} };
  window.addEventListener("resize", onResize);
  currentTerm = { term, ws, onResize };
}
function closeTerminal(){
  if(!currentTerm) return;
  window.removeEventListener("resize", currentTerm.onResize);
  try{ currentTerm.ws.close(); }catch{}
  try{ currentTerm.term.dispose(); }catch{}
  currentTerm=null; $("#term").hidden=true;
}

// ---- VNC (noVNC, dynamically imported) ----
let currentVnc = null;
async function openVnc(id){
  if(!id || !secure) return;
  let RFB;
  try { RFB = (await import("/novnc.js")).default; }
  catch { $("#mtoast").textContent="failed to load VNC"; return; }
  $("#modal").hidden = true;
  $("#vnctitle").textContent = "VNC · " + (deviceName(id) || id);
  $("#vnc").hidden = false;
  const box = $("#vncbox"); box.innerHTML = "";
  const rfb = new RFB(box, `wss://${location.host}/ws/vnc?id=${encodeURIComponent(id)}`);
  rfb.scaleViewport = true; rfb.background = "#000";
  rfb.addEventListener("credentialsrequired", ()=>{ rfb.sendCredentials({ password: prompt("VNC password:") || "" }); });
  currentVnc = { rfb };
}
function closeVnc(){
  if(!currentVnc) return;
  try{ currentVnc.rfb.disconnect(); }catch{}
  currentVnc=null; $("#vnc").hidden=true; $("#vncbox").innerHTML="";
}

// ---- topology map ----
function clip(s,n){ s=s||""; return s.length>n ? s.slice(0,n-1)+"…" : s; }
function applyVB(){ $("#mapsvg").setAttribute("viewBox", `${vb.x} ${vb.y} ${vb.w} ${vb.h}`); }
function renderMap(g){
  const svg = $("#mapsvg");
  if(!g.nodes || !g.nodes.length){ svg.innerHTML=""; $("#mapstatus").textContent="empty — run a scan first"; return; }
  const pad=40;
  const minX=Math.min(...g.nodes.map(n=>n.x))-pad, minY=Math.min(...g.nodes.map(n=>n.y))-pad;
  const maxX=Math.max(...g.nodes.map(n=>n.x+n.w))+pad, maxY=Math.max(...g.nodes.map(n=>n.y+n.h))+pad;
  const byKey={}; g.nodes.forEach(n=>byKey[n.key]=n);
  const edges = (g.edges||[]).map(e=>{
    const a=byKey[e.from], b=byKey[e.to]; if(!a||!b) return "";
    return `<line x1="${a.x+a.w/2}" y1="${a.y+a.h/2}" x2="${b.x+b.w/2}" y2="${b.y+b.h/2}" stroke="#888" stroke-opacity="0.5" stroke-width="1.5"/>`;
  }).join("");
  const nodes = g.nodes.map(n=>{
    const tx=n.x+11;
    return `<g class="node" data-id="${esc(n.deviceId)}">`
      + `<rect x="${n.x}" y="${n.y}" width="${n.w}" height="${n.h}" rx="7" fill="${esc(n.fill)}" stroke="${esc(n.line)}"/>`
      // Same five lines in the same order as the desktop map: category, name, vendor, model, address -
      // then the MAC. The category leads so the boxes can be sorted by eye in one pass down the map.
      // Vendor and model are separate lines here too; combined they overflowed the box and the clip()
      // below ate the model, which is the half that identifies the device.
      + (n.kind?`<text x="${tx}" y="${n.y+16}" fill="${esc(n.text)}" font-size="9" opacity="0.7">${esc(clip(n.kind,26))}</text>`:"")
      + `<text x="${tx}" y="${n.y+31}" fill="${esc(n.text)}" font-size="12" font-weight="600">${esc(clip(n.title,22))}</text>`
      + (n.vendor?`<text x="${tx}" y="${n.y+46}" fill="${esc(n.text)}" font-size="10" opacity="0.9">${esc(clip(n.vendor,26))}</text>`:"")
      + (n.model?`<text x="${tx}" y="${n.y+59}" fill="${esc(n.text)}" font-size="10" opacity="0.9">${esc(clip(n.model,26))}</text>`:"")
      + (n.detail?`<text x="${tx}" y="${n.y+74}" fill="${esc(n.text)}" font-size="10" opacity="0.85">${esc(clip(n.detail,28))}</text>`:"")
      + (n.mac?`<text x="${tx}" y="${n.y+87}" fill="${esc(n.text)}" font-size="9" opacity="0.5">${esc(n.mac)}</text>`:"")
      + `</g>`;
  }).join("");
  svg.innerHTML = edges + nodes;
  vb = { x:minX, y:minY, w:maxX-minX, h:maxY-minY };
  applyVB();
  $("#mapstatus").textContent = g.nodes.length + " nodes";
}
async function loadMap(){
  $("#mapstatus").textContent = "loading…";
  try { renderMap(await j("/api/topology?view="+(mapPhysical?"physical":"logical"))); mapLoaded=true; }
  catch(e){ $("#mapstatus").textContent = "failed"; }
}
async function loadIpv6(){
  try { v6rows = await j("/api/ipv6"); renderIpv6(); } catch(e){}
}

// Four named views, matching the desktop app. The map is one element reused by the two map views – the
// difference between them is only which graph is fetched, so mapPhysical decides and a switch reloads.
let view = "ipv4";
function showView(v){
  view = v;
  $("#devicesView").hidden = v !== "ipv4";
  $("#ipv6View").hidden    = v !== "ipv6";
  $("#mapView").hidden     = v !== "dist" && v !== "topo";
  $("#tabIpv4").classList.toggle("on", v === "ipv4");
  $("#tabIpv6").classList.toggle("on", v === "ipv6");
  $("#tabDist").classList.toggle("on", v === "dist");
  $("#tabTopo").classList.toggle("on", v === "topo");
  if(v === "ipv6") loadIpv6();
  if(v === "dist" || v === "topo"){
    const wantPhysical = v === "topo";
    if(wantPhysical !== mapPhysical || !mapLoaded){ mapPhysical = wantPhysical; loadMap(); }
  }
}
(function(){
  const svg = $("#mapsvg"); let drag=null, moved=false;
  svg.addEventListener("wheel", e=>{
    e.preventDefault(); const r=svg.getBoundingClientRect();
    const mx=vb.x+(e.clientX-r.left)/r.width*vb.w, my=vb.y+(e.clientY-r.top)/r.height*vb.h;
    const f = e.deltaY<0 ? 0.85 : 1.18; vb.w*=f; vb.h*=f;
    vb.x = mx-(e.clientX-r.left)/r.width*vb.w; vb.y = my-(e.clientY-r.top)/r.height*vb.h; applyVB();
  }, {passive:false});
  svg.addEventListener("pointerdown", e=>{ drag={x:e.clientX,y:e.clientY,vx:vb.x,vy:vb.y}; moved=false;
    svg.classList.add("grabbing"); svg.setPointerCapture(e.pointerId); });
  svg.addEventListener("pointermove", e=>{ if(!drag) return; const r=svg.getBoundingClientRect();
    if(Math.abs(e.clientX-drag.x)+Math.abs(e.clientY-drag.y)>3) moved=true;
    vb.x = drag.vx-(e.clientX-drag.x)/r.width*vb.w; vb.y = drag.vy-(e.clientY-drag.y)/r.height*vb.h; applyVB(); });
  const end=()=>{ drag=null; svg.classList.remove("grabbing"); };
  svg.addEventListener("pointerup", end); svg.addEventListener("pointercancel", end);
  svg.addEventListener("click", e=>{ if(moved){ moved=false; return; }
    const g=e.target.closest(".node"); if(g && g.dataset.id) openDetail(g.dataset.id); });
})();
$("#tabIpv4").onclick = ()=> showView("ipv4");
$("#tabIpv6").onclick = ()=> showView("ipv6");
$("#tabDist").onclick = ()=> showView("dist");
$("#tabTopo").onclick = ()=> showView("topo");
$("#mapRefresh").onclick = loadMap;

document.querySelectorAll("th[data-k]").forEach(th=>th.onclick=()=>{
  const k=th.dataset.k; if(sortKey===k) sortDir*=-1; else {sortKey=k; sortDir=1;} render();
});
// One filter box drives whichever table is showing.
$("#filter").oninput = ()=>{ render(); renderIpv6(); };
$("#scan").onclick = scanNow;
$("#rows").addEventListener("click", e=>{ if(badgeAct(e)) return; const tr=e.target.closest("tr[data-id]"); if(tr) openDetail(tr.dataset.id); });
// Same on the IPv6 table: an ssh/vnc chip connects, anything else on the row opens the detail.
$("#v6rows").addEventListener("click", e=>{ if(badgeAct(e)) return; const tr=e.target.closest("tr[data-id]"); if(tr) openDetail(tr.dataset.id); });
$("#mclose").onclick = ()=>{ $("#modal").hidden=true; };
$("#modal").onclick = e=>{ if(e.target.id==="modal") $("#modal").hidden=true; };
$("#mwake").onclick = ()=> wake($("#mwake").dataset.id);
$("#mlsave").onclick = ()=> saveLogin($("#modal").dataset.id);
$("#mbrsc").onclick = ()=> backup($("#modal").dataset.id, false);
$("#mbfull").onclick = ()=> backup($("#modal").dataset.id, true);
// The terminal and VNC are opened from the ssh / vnc chips in the list (see badgeAct); only their close
// buttons are wired here.
$("#termclose").onclick = closeTerminal;
$("#vncclose").onclick = closeVnc;
loadInfo(); tick(); pollStatus();
setInterval(tick, 4000); setInterval(pollStatus, 1200);
</script>
</body>
</html>
""";
}
