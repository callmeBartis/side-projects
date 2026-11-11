// Claude usage home screen widget for the Scriptable app (iOS).
// Reads the status topic the Windows app publishes to ntfy.sh.
// TOPIC: right-click the taskbar widget > "ntfy-Topic kopieren".
const TOPIC = "PASTE-TOPIC-HERE";

const ORANGE = new Color("#D97757"), AMBER = new Color("#F2B33D"), RED = new Color("#E5484D");
const FG = Color.dynamic(new Color("#1d1d1f"), new Color("#f5f5f7"));
const DIM = Color.dynamic(new Color("#6e6e73"), new Color("#98989d"));

let msg = null;
try {
  const body = await new Request(`https://ntfy.sh/${TOPIC}-status/json?poll=1&since=latest`).loadString();
  const line = body.trim().split("\n").pop();
  if (line) msg = JSON.parse(line);
} catch (e) { /* offline: show the empty state below */ }

const w = new ListWidget();
w.backgroundColor = Color.dynamic(new Color("#ffffff"), new Color("#1c1c1e"));
w.url = "https://claude.ai/settings/usage";
w.setPadding(14, 14, 14, 14);

const title = w.addText("Claude");
title.font = Font.semiboldSystemFont(13);
title.textColor = DIM;
w.addSpacer(8);

if (!msg) {
  const t = w.addText("Keine Daten.\nPC aus?");
  t.font = Font.systemFont(13);
  t.textColor = DIM;
} else {
  row("Session", msg.message.match(/Session (\d+) % \(Reset ([^)]+)\)/));
  w.addSpacer(10);
  row("Woche", msg.message.match(/Woche (\d+) % \(Reset ([^)]+)\)/));
  w.addSpacer();
  // The PC publishes at least hourly, so anything older means it is off or offline.
  const ageMin = (Date.now() / 1000 - msg.time) / 60;
  const at = new Date(msg.time * 1000).toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" });
  const foot = w.addText(ageMin > 75 ? `Stand ${at}, PC offline?` : `Stand ${at}`);
  foot.font = Font.systemFont(10);
  foot.textColor = ageMin > 75 ? RED : DIM;
}

Script.setWidget(w);
if (!config.runsInWidget) await w.presentSmall();
Script.complete();

function row(label, m) {
  const pct = m ? Number(m[1]) : 0;
  const head = w.addStack();
  head.centerAlignContent();
  const l = head.addText(label);
  l.font = Font.systemFont(12);
  l.textColor = DIM;
  head.addSpacer();
  const p = head.addText(`${pct} %`);
  p.font = Font.boldSystemFont(15);
  p.textColor = FG;
  w.addSpacer(3);
  w.addImage(bar(pct)).imageSize = new Size(130, 5);
  if (m) {
    const r = w.addText(`Reset ${m[2]}`);
    r.font = Font.systemFont(9);
    r.textColor = DIM;
  }
}

function bar(pct) {
  const W = 260, H = 10;
  const d = new DrawContext();
  d.size = new Size(W, H);
  d.opaque = false;
  d.respectScreenScale = true;
  const track = new Path();
  track.addRoundedRect(new Rect(0, 0, W, H), H / 2, H / 2);
  d.addPath(track);
  d.setFillColor(new Color("#8e8e93", 0.25));
  d.fillPath();
  if (pct > 0) {
    const fill = new Path();
    fill.addRoundedRect(new Rect(0, 0, Math.max(H, W * Math.min(pct, 100) / 100), H), H / 2, H / 2);
    d.addPath(fill);
    d.setFillColor(pct >= 95 ? RED : pct >= 80 ? AMBER : ORANGE);
    d.fillPath();
  }
  return d.getImage();
}
