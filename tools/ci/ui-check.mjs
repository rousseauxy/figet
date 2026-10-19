// Drives a real browser through a fresh FiGet instance and fails when a page stops working in one. The integration tests
// read the pages as HTML; what only a browser does - apply the content policy, run app.js, lay a page out at phone width -
// is checked here, against the built image.
//
//   node tools/ci/ui-check.mjs <base url> <chrome or edge executable>
//
// The instance must be new: the script signs in with the first administrator's start password and replaces it. No
// package beyond Node itself: the browser is driven over the DevTools protocol with Node's own fetch and WebSocket.
import { spawn } from "node:child_process";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const [base, browserPath] = process.argv.slice(2);
if (!base || !browserPath) {
  console.error("usage: node ui-check.mjs <base url> <browser executable>");
  process.exit(2);
}

const work = mkdtempSync(join(tmpdir(), "figet-ui-"));
const uploadPath = join(work, "ui-check.txt");
const uploadText = `uploaded by the browser check at ${new Date().toISOString()}\n`;
writeFileSync(uploadPath, uploadText);

const port = 9333;
const browser = spawn(browserPath, [
  "--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${join(work, "profile")}`,
  "--no-first-run", "--no-default-browser-check", "--window-size=1280,900", "about:blank",
], { stdio: "ignore" });

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const results = [];
let step = "start";

function finish(code) {
  for (const line of results) console.log(line);
  browser.kill();
  try { rmSync(work, { recursive: true, force: true }); } catch { /* the browser may still hold its profile */ }
  process.exit(code);
}

// A browser that hangs must fail the job, not hold it until the job's own timeout.
setTimeout(() => { results.push(`FAIL  finished in time  (stuck at: ${step})`); finish(1); }, 180_000);

let page;
for (let i = 0; i < 100 && !page; i++) {
  try { page = (await (await fetch(`http://127.0.0.1:${port}/json`)).json()).find((t) => t.type === "page"); } catch { /* not up yet */ }
  if (!page) await sleep(200);
}
if (!page) { results.push("FAIL  browser started"); finish(1); }

const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve) => socket.addEventListener("open", resolve));

let lastId = 0;
const pending = new Map();
const waiters = [];
const violations = [];
const scriptErrors = [];
socket.addEventListener("message", (message) => {
  const data = JSON.parse(message.data);
  if (data.id && pending.has(data.id)) { pending.get(data.id)(data.result ?? {}); pending.delete(data.id); return; }
  if (data.method === "Log.entryAdded" && /Content Security Policy/i.test(data.params.entry.text)) violations.push(`${step}: ${data.params.entry.text}`);
  if (data.method === "Runtime.exceptionThrown") scriptErrors.push(`${step}: ${data.params.exceptionDetails.exception?.description ?? data.params.exceptionDetails.text}`);
  for (const waiter of [...waiters]) {
    if (waiter.method === data.method) { waiters.splice(waiters.indexOf(waiter), 1); waiter.resolve(data.params); }
  }
});

const send = (method, params = {}) => new Promise((resolve) => {
  const id = ++lastId;
  pending.set(id, resolve);
  socket.send(JSON.stringify({ id, method, params }));
});
const next = (method, ms = 15_000) => new Promise((resolve) => {
  const waiter = { method, resolve };
  waiters.push(waiter);
  setTimeout(() => { const i = waiters.indexOf(waiter); if (i >= 0) { waiters.splice(i, 1); resolve(null); } }, ms);
});
const evaluate = async (expression) =>
  (await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true })).result?.value;
const where = () => evaluate("location.pathname");
const settle = async (loaded) => { await loaded; await sleep(150); };

async function go(path) {
  step = `open ${path}`;
  const loaded = next("Page.loadEventFired");
  await send("Page.navigate", { url: base + path });
  await settle(loaded);
}

// Submits the form that holds the given field. Never "the first submit button on the page": for a signed-in reader that
// is the menu's Sign out.
async function submitFormOf(fieldId, fill) {
  step = `submit the form of #${fieldId}`;
  const loaded = next("Page.loadEventFired");
  await evaluate(`(() => { ${fill}; const f = document.getElementById(${JSON.stringify(fieldId)}).form; f.requestSubmit(f.querySelector("button[type=submit]")); })()`);
  await settle(loaded);
}

const check = (name, ok, detail = "") => results.push(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? `  (${detail})` : ""}`);
const set = (id, value) => `document.getElementById(${JSON.stringify(id)}).value = ${JSON.stringify(value)}`;

await send("Page.enable");
await send("Log.enable");
await send("Runtime.enable");

// ── Sign in as the first administrator and replace the start password ──────────────────────────────────────────────
const password = `Ui-check-${Date.now()}!`;
await go("/account/login/local");
await submitFormOf("username", `${set("username", "admin")}; ${set("password", "admin")}`);
if ((await where()).startsWith("/account/password")) {
  await submitFormOf("new-password", `${set("current-password", "admin")}; ${set("new-password", password)}; ${set("confirm-password", password)}`);
}
check("first administrator signs in and replaces the start password", !(await where()).startsWith("/account"), await where());

// ── A feed and an asset directory, made through their forms ─────────────────────────────────────────────────────────
for (const [list, name, route] of [["/admin/feeds", "ui-feed", "/feeds/ui-feed"], ["/admin/assets", "ui-files", "/assets/ui-files"]]) {
  await go(list);
  await submitFormOf("feed-name", set("feed-name", name));
  await go(route);
  check(`${route} exists after the create form`, (await evaluate(`document.querySelector("h1")?.textContent ?? ""`)).includes(name));
}

// ── Light and dark: the toggle flips, the choice is stored and applied again before paint ──────────────────────────
await go("/");
const before = await evaluate(`document.documentElement.getAttribute("data-theme")`);
await evaluate(`document.querySelector("[data-theme-toggle]").click()`);
const chosen = await evaluate(`document.documentElement.getAttribute("data-theme")`);
await go("/");
const reloaded = await evaluate(`document.documentElement.getAttribute("data-theme")`);
check("theme toggle flips and survives a reload", chosen !== null && chosen !== before && reloaded === chosen, `${before} -> ${chosen}, reloaded ${reloaded}`);
check("a feed's dot takes its colour from its class",
  await evaluate(`getComputedStyle(document.querySelector(".fg-series-dot")).getPropertyValue("--series").trim() !== ""`));

// ── Keyboard: the skip link is the first stop and lands on the page ────────────────────────────────────────────────
step = "skip link";
await send("Input.dispatchKeyEvent", { type: "keyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
await send("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
check("skip link is the first Tab stop and shows when focused",
  await evaluate(`document.activeElement.classList.contains("fg-skip") && document.activeElement.getBoundingClientRect().top >= 0`));
await evaluate(`document.activeElement.click()`);
check("skip link moves focus to the page", await evaluate(`document.activeElement === document.querySelector("main")`));

// ── A tooltip is drawn and placed (app.js positions it through element.style) ──────────────────────────────────────
step = "tooltip";
await evaluate(`document.querySelector(".fg-copy").dispatchEvent(new MouseEvent("mouseover", { bubbles: true }))`);
await sleep(900);
check("tooltip shows and is placed", await evaluate(`(() => { const t = document.querySelector(".fg-tooltip"); return !!t && !t.hidden && t.style.top !== ""; })()`));

// ── Asset directory: upload through the drop zone, make a download link, ask before deleting ──────────────────────
await go("/assets/ui-files");
step = "upload";
const { root } = await send("DOM.getDocument");
const { nodeId } = await send("DOM.querySelector", { nodeId: root.nodeId, selector: "input[data-asset-files]" });
const afterUpload = next("Page.loadEventFired");
await send("DOM.setFileInputFiles", { nodeId, files: [uploadPath] });
await settle(afterUpload);
check("a file uploads through the drop zone", await evaluate(`document.body.textContent.includes("ui-check.txt")`));

step = "download link";
const afterLink = next("Page.loadEventFired");
await evaluate(`(() => { const b = document.querySelector('button[aria-label="Make a download link for ui-check.txt"]'); b.form.requestSubmit(b); })()`);
await settle(afterLink);
const shareUrl = await evaluate(`document.querySelector(".fg-share-link .fg-copy")?.getAttribute("data-copy") ?? ""`);
const shared = shareUrl ? await evaluate(`fetch(${JSON.stringify(shareUrl)}, { credentials: "omit" }).then(r => r.ok ? r.text() : "HTTP " + r.status)`) : "";
check("the download link serves the file to someone not signed in", shared === uploadText, shareUrl ? `got ${JSON.stringify(shared).slice(0, 60)}` : "no link on the page");

step = "confirm dialog";
await evaluate(`(() => { const f = [...document.querySelectorAll("form[data-confirm]")][0]; f.requestSubmit(f.querySelector("button[type=submit]")); })()`);
await sleep(300);
check("a delete asks first, in the page's own dialog", await evaluate(`!!document.querySelector("dialog.fg-confirm[open]")`));
await evaluate(`document.querySelector("dialog.fg-confirm [data-dialog-close]").click()`);
await sleep(200);
check("cancelling the question deletes nothing", await evaluate(`!document.querySelector("dialog.fg-confirm[open]") && document.body.textContent.includes("ui-check.txt")`));

// ── Every page an administrator reaches, for policy violations and script errors ───────────────────────────────────
const pages = [
  "/", "/assets", "/assets/ui-files", "/feeds/ui-feed", "/feeds/ui-feed/changes", "/account/profile",
  "/admin/feeds", "/admin/assets", "/admin/feeds/ui-feed", "/admin/feeds/ui-feed/access", "/admin/feeds/ui-feed/upstreams",
  "/admin/feeds/ui-feed/retention", "/admin/feeds/ui-feed/name", "/admin/feeds/ui-feed/instructions",
  "/admin/feeds/ui-feed/unlisted", "/admin/users", "/admin/groups", "/admin/providers", "/admin/tokens", "/admin/changes",
  "/admin/audit", "/admin/storage", "/admin/system", "/admin/appearance", "/no-such-page",
];
for (const path of pages) await go(path);

// ── Phone width: no page may scroll sideways as a whole (a wide table scrolls inside its own box) ──────────────────
await send("Emulation.setDeviceMetricsOverride", { width: 390, height: 844, deviceScaleFactor: 3, mobile: true });
const wide = [];
let measuredWidth = 0;
for (const path of ["/", "/assets", "/assets/ui-files", "/feeds/ui-feed", "/account/profile", "/admin/feeds", "/admin/feeds/ui-feed", "/admin/tokens", "/admin/system"]) {
  await go(path);
  measuredWidth = await evaluate(`document.documentElement.clientWidth`);
  const overflow = await evaluate(`document.documentElement.scrollWidth - document.documentElement.clientWidth`);
  if (overflow > 1) wide.push(`${path} +${overflow}px`);
}
check("no page scrolls sideways at phone width", measuredWidth > 0 && measuredWidth <= 400 && wide.length === 0, `${measuredWidth}px wide${wide.length ? "; " + wide.join(", ") : ""}`);

check("no content-policy violation on any page", violations.length === 0, violations.slice(0, 3).join(" | "));
check("no script error on any page", scriptErrors.length === 0, scriptErrors.slice(0, 3).join(" | "));
finish(results.some((line) => line.startsWith("FAIL")) ? 1 : 0);
