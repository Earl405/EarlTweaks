// EARL TWEAKS admin backend on Cloudflare Workers (free plan). Replaces the Firebase Cloud Function so no Blaze plan is needed.
// Secrets (set in Cloudflare, NEVER in the app): SERVICE_ACCOUNT_JSON, API_KEY, PROJECT_ID
// Every request must carry the caller's Firebase ID token. The caller is re-checked against Firebase on each call
// (valid, not revoked, not disabled, role = Admin) before anything privileged runs.
const EMAIL_DOMAIN = "earltweaks.example"; // must match CloudConfig.EmailDomain
const toEmail = (u) => `${u.toLowerCase()}@${EMAIL_DOMAIN}`;
const IDT = "https://identitytoolkit.googleapis.com";

class HttpError extends Error { constructor(code, msg) { super(msg); this.code = code; } }

function validateUsername(u) {
  u = (u || "").trim();
  if (u.length < 3 || u.length > 32) return "Username must be 3 to 32 characters.";
  if (!/^[A-Za-z0-9_.-]+$/.test(u)) return "Username may only use letters, numbers, _ . and -";
  return null;
}
function validatePassword(p) {
  p = p || "";
  if (p.length < 12) return "Password must be at least 12 characters.";
  if (p.length > 128) return "Password is too long (128 characters maximum).";
  const kinds = [/[a-z]/, /[A-Z]/, /[0-9]/, /[^A-Za-z0-9]/].filter((r) => r.test(p)).length;
  if (kinds < 3) return "Use at least 3 kinds of characters: lowercase, uppercase, numbers, symbols.";
  return null;
}

// ---- service-account access token (RS256 JWT -> OAuth token), cached ----
let cached = { token: "", exp: 0 };
const b64u = (buf) => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const b64uStr = (s) => b64u(new TextEncoder().encode(s));

async function accessToken(env) {
  if (Date.now() < cached.exp) return cached.token;
  const sa = JSON.parse(env.SERVICE_ACCOUNT_JSON);
  const now = Math.floor(Date.now() / 1000);
  const head = b64uStr(JSON.stringify({ alg: "RS256", typ: "JWT" }));
  const claims = b64uStr(JSON.stringify({
    iss: sa.client_email, scope: "https://www.googleapis.com/auth/identitytoolkit https://www.googleapis.com/auth/cloud-platform",
    aud: "https://oauth2.googleapis.com/token", iat: now, exp: now + 3600 }));
  const der = Uint8Array.from(atob(sa.private_key.replace(/-----[A-Z ]+-----/g, "").replace(/\s+/g, "")), (c) => c.charCodeAt(0));
  const key = await crypto.subtle.importKey("pkcs8", der, { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["sign"]);
  const sig = await crypto.subtle.sign("RSASSA-PKCS1-v1_5", key, new TextEncoder().encode(`${head}.${claims}`));
  const r = await fetch("https://oauth2.googleapis.com/token", {
    method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "urn:ietf:params:oauth:grant-type:jwt-bearer", assertion: `${head}.${claims}.${b64u(sig)}` }) });
  const j = await r.json();
  if (!r.ok) throw new Error("Service account auth failed");
  cached = { token: j.access_token, exp: Date.now() + (j.expires_in - 120) * 1000 };
  return cached.token;
}

async function idt(env, path, body, method = "POST") {
  const r = await fetch(`${IDT}/v1/projects/${env.PROJECT_ID}/${path}`, {
    method, headers: { Authorization: `Bearer ${await accessToken(env)}`, "Content-Type": "application/json" },
    body: method === "GET" ? undefined : JSON.stringify(body) });
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(j?.error?.message || `Identity Toolkit error ${r.status}`);
  return j;
}

const roleOf = (u) => { try { return JSON.parse(u.customAttributes || "{}").role === "Admin" ? "Admin" : "User"; } catch { return "User"; } };
const usernameOf = (u) => u.displayName || (u.email || "").split("@")[0];
const iso = (ms) => (ms ? new Date(Number(ms)).toISOString() : null);

async function requireAdmin(req, env) {
  const m = /^Bearer (.+)$/.exec(req.headers.get("Authorization") || "");
  if (!m) throw new HttpError(401, "Sign in required.");
  const r = await fetch(`${IDT}/v1/accounts:lookup?key=${env.API_KEY}`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ idToken: m[1] }) });
  if (!r.ok) throw new HttpError(401, "Session expired. Please sign in again.");
  const u = (await r.json()).users?.[0];
  if (!u || u.disabled || roleOf(u) !== "Admin") throw new HttpError(403, "Administrator access is required.");
  return u;
}

async function findByUsername(env, name) {
  const j = await idt(env, "accounts:lookup", { email: [toEmail(name)] });
  return j.users?.[0] || null;
}
async function allUsers(env) {
  const out = []; let page = "";
  do {
    const j = await idt(env, `accounts:batchGet?maxResults=1000${page ? "&nextPageToken=" + encodeURIComponent(page) : ""}`, null, "GET");
    out.push(...(j.users || [])); page = j.nextPageToken || "";
  } while (page);
  return out.filter((u) => (u.email || "").endsWith("@" + EMAIL_DOMAIN));
}
const activeAdmins = (users, exceptId) => users.filter((u) => roleOf(u) === "Admin" && !u.disabled && u.localId !== exceptId).length;

const actions = {
  async list(_c, _b, env) {
    return { accounts: (await allUsers(env)).map((u) => ({
      username: usernameOf(u), role: roleOf(u), disabled: !!u.disabled, created: iso(u.createdAt), lastLogin: iso(u.lastLoginAt) })) };
  },
  async exists(_c, b, env) { return { exists: !!(await findByUsername(env, (b.username || "").trim())) }; },
  async create(_c, b, env) {
    const name = (b.username || "").trim();
    const err = validateUsername(name) || validatePassword(b.password);
    if (err) throw new HttpError(400, err);
    const role = b.role === "Admin" ? "Admin" : "User";
    let u;
    try { u = await idt(env, "accounts", { email: toEmail(name), password: b.password, displayName: name }); }
    catch (e) { if (/EMAIL_EXISTS/.test(e.message)) throw new HttpError(409, "That username already exists."); throw e; }
    await idt(env, "accounts:update", { localId: u.localId, customAttributes: JSON.stringify({ role }) });
    return { ok: true, username: name, role };
  },
  async setDisabled(c, b, env) {
    const u = await findByUsername(env, b.username || ""); if (!u) throw new HttpError(404, "Account not found.");
    if (u.localId === c.localId) throw new HttpError(400, "You cannot disable your own account.");
    if (b.disabled && roleOf(u) === "Admin" && activeAdmins(await allUsers(env), u.localId) === 0)
      throw new HttpError(400, "At least one active administrator must remain.");
    const body = { localId: u.localId, disableUser: !!b.disabled };
    if (b.disabled) body.validSince = String(Math.floor(Date.now() / 1000));
    await idt(env, "accounts:update", body);
    return { ok: true };
  },
  async resetPassword(_c, b, env) {
    const err = validatePassword(b.password); if (err) throw new HttpError(400, err);
    const u = await findByUsername(env, b.username || ""); if (!u) throw new HttpError(404, "Account not found.");
    await idt(env, "accounts:update", { localId: u.localId, password: b.password, validSince: String(Math.floor(Date.now() / 1000)) });
    return { ok: true };
  },
  async delete(c, b, env) {
    const u = await findByUsername(env, b.username || ""); if (!u) throw new HttpError(404, "Account not found.");
    if (u.localId === c.localId) throw new HttpError(400, "You cannot delete your own account.");
    if (roleOf(u) === "Admin" && activeAdmins(await allUsers(env), u.localId) === 0)
      throw new HttpError(400, "At least one active administrator must remain.");
    await idt(env, "accounts:delete", { localId: u.localId });
    return { ok: true };
  },
};

export default {
  async fetch(req, env) {
    const send = (code, obj) => new Response(JSON.stringify(obj), { status: code, headers: { "Content-Type": "application/json" } });
    try {
      if (req.method !== "POST") throw new HttpError(405, "POST only.");
      const caller = await requireAdmin(req, env);
      const body = await req.json().catch(() => ({}));
      const fn = actions[body.action];
      if (!fn) throw new HttpError(400, "Unknown action.");
      return send(200, await fn(caller, body, env));
    } catch (e) {
      if (!(e instanceof HttpError)) console.error(e.message);
      return send(e instanceof HttpError ? e.code : 500, { error: e instanceof HttpError ? e.message : "Server error." });
    }
  },
};
