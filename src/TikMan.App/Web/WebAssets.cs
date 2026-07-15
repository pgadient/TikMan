namespace TikMan.App.Web;

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
<title>TikMan</title>
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
  .prog { padding:0 18px 8px; display:flex; align-items:center; gap:10px; }
  .track { flex:1; height:8px; border-radius:6px; background:var(--line); overflow:hidden; }
  .fill { height:100%; width:0; background:var(--gw); border-radius:6px; transition:width .3s ease; }
  .fill.indet { width:35% !important; animation:slide 1.1s ease-in-out infinite; }
  @keyframes slide { 0%{margin-left:-35%} 100%{margin-left:100%} }
  .pphase { color:var(--muted); font-size:12px; white-space:nowrap; }
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
  .muted { color:var(--muted); }
  .empty { padding:40px; text-align:center; color:var(--muted); }
  footer { padding:14px 18px; color:var(--muted); font-size:12px; }
</style>
</head>
<body>
<header>
  <h1 id="title">TikMan</h1><span class="ver" id="ver"></span>
  <span class="count" id="count"></span>
</header>
<div class="bar">
  <button id="scan">⟳ Scan</button>
  <input type="search" id="filter" placeholder="Filter… (name, IP, MAC, vendor, type)" autocomplete="off">
  <span class="muted" id="status"></span>
</div>
<div class="prog" id="prog" hidden>
  <div class="track"><div class="fill" id="pfill"></div></div>
  <span class="pphase" id="pphase"></span>
</div>
<div class="wrap">
  <table>
    <thead><tr>
      <th data-k="name">Name</th><th data-k="ip">IP</th><th data-k="type">Type</th>
      <th data-k="vendor">Vendor</th><th data-k="model">Model</th><th data-k="mac">MAC</th>
      <th data-k="status">Status</th>
    </tr></thead>
    <tbody id="rows"></tbody>
  </table>
  <div class="empty" id="empty" hidden>No devices yet — start a scan in the desktop app.</div>
</div>
<footer>TikMan web · live</footer>
<script>
let devices = [], sortKey = "ip", sortDir = 1;
const $ = s => document.querySelector(s);

async function j(url){ const r = await fetch(url,{cache:"no-store"}); if(!r.ok) throw new Error(r.status); return r.json(); }

async function loadInfo(){
  try { const i = await j("/api/info"); $("#title").textContent = i.title || "TikMan";
        $("#ver").textContent = i.version ? "v"+i.version : ""; document.title = (i.title||"TikMan"); } catch{}
}

function ipKey(ip){ const m=(ip||"").match(/(\d+)\.(\d+)\.(\d+)\.(\d+)/);
  return m ? ((+m[1]<<24)>>>0)+(+m[2]<<16)+(+m[3]<<8)+(+m[4]) : 1e12 + (ip||"").localeCompare(""); }

function render(){
  const f = $("#filter").value.trim().toLowerCase();
  let list = devices.filter(d => !f ||
    [d.name,d.ip,d.mac,d.vendor,d.type,d.model,d.status].some(v => (v||"").toLowerCase().includes(f)));
  list.sort((a,b)=>{
    let x,y;
    if(sortKey==="ip"){ x=ipKey(a.ip); y=ipKey(b.ip); }
    else { x=(a[sortKey]||"").toLowerCase(); y=(b[sortKey]||"").toLowerCase(); }
    return (x<y?-1:x>y?1:0)*sortDir;
  });
  $("#count").textContent = devices.length + " devices" + (f? " · "+list.length+" shown":"");
  $("#empty").hidden = devices.length>0;
  $("#rows").innerHTML = list.map(d => `<tr>
    <td class="name">${esc(d.name)||'<span class="muted">—</span>'} ${d.hasLogin?'<span class="lock" title="has login">🔑</span>':''}</td>
    <td>${esc(d.ip)}</td>
    <td>${d.type?`<span class="tag ${d.isGateway?'gw':''}">${esc(d.type)}</span>`:''}</td>
    <td>${esc(d.vendor)}</td><td>${esc(d.model)}</td>
    <td class="muted">${esc(d.mac)}</td><td>${esc(d.status)}</td></tr>`).join("");
}
function esc(s){ return (s||"").replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])); }

async function tick(){
  try { devices = await j("/api/devices"); $("#status").textContent=""; render(); }
  catch(e){ $("#status").textContent = "connection lost…"; }
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
  } catch(e){ $("#status").textContent = "connection lost…"; }
}
async function scanNow(){ try { await fetch("/api/scan",{method:"POST"}); pollStatus(); } catch{} }

document.querySelectorAll("th[data-k]").forEach(th=>th.onclick=()=>{
  const k=th.dataset.k; if(sortKey===k) sortDir*=-1; else {sortKey=k; sortDir=1;} render();
});
$("#filter").oninput = render;
$("#scan").onclick = scanNow;
loadInfo(); tick(); pollStatus();
setInterval(tick, 4000); setInterval(pollStatus, 1200);
</script>
</body>
</html>
""";
}
