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
    <div class="mfoot"><button id="mwake" hidden>⏻ Wake</button><span class="muted" id="mtoast"></span></div>
  </div>
</div>
<footer>TikMan web · live · tap a row for details</footer>
<script>
let devices = [], sortKey = "ip", sortDir = 1;
const secure = location.protocol === "https:";
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
  $("#rows").innerHTML = list.map(d => `<tr class="row" data-id="${esc(d.id)}">
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

document.querySelectorAll("th[data-k]").forEach(th=>th.onclick=()=>{
  const k=th.dataset.k; if(sortKey===k) sortDir*=-1; else {sortKey=k; sortDir=1;} render();
});
$("#filter").oninput = render;
$("#scan").onclick = scanNow;
$("#rows").addEventListener("click", e=>{ const tr=e.target.closest("tr[data-id]"); if(tr) openDetail(tr.dataset.id); });
$("#mclose").onclick = ()=>{ $("#modal").hidden=true; };
$("#modal").onclick = e=>{ if(e.target.id==="modal") $("#modal").hidden=true; };
$("#mwake").onclick = ()=> wake($("#mwake").dataset.id);
$("#mlsave").onclick = ()=> saveLogin($("#modal").dataset.id);
$("#mbrsc").onclick = ()=> backup($("#modal").dataset.id, false);
$("#mbfull").onclick = ()=> backup($("#modal").dataset.id, true);
loadInfo(); tick(); pollStatus();
setInterval(tick, 4000); setInterval(pollStatus, 1200);
</script>
</body>
</html>
""";
}
