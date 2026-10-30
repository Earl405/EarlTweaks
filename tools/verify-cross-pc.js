// End-to-end check of the 7 required scenarios against your REAL Firebase project, using only REST calls with
// no shared local state, exactly as two separate PCs would. Needs Node 18+.
//   node verify-cross-pc.js <apiKey> <workerUrl> <adminUser> <adminPassword>
const [API_KEY, ADMIN_API_URL, ADMIN_USER, ADMIN_PW] = process.argv.slice(2);
if (!ADMIN_PW) { console.error("Usage: node verify-cross-pc.js <apiKey> <workerUrl> <adminUser> <adminPassword>"); process.exit(1); }
const DOMAIN = "earltweaks.example";
const FN = ADMIN_API_URL.replace(/\/$/, "");
const signIn = (u, p) => fetch(`https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key=${API_KEY}`, {
  method: "POST", headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ email: `${u.toLowerCase()}@${DOMAIN}`, password: p, returnSecureToken: true }) }).then(async (r) => ({ ok: r.ok, j: await r.json() }));
const api = (tok, body) => fetch(FN, { method: "POST", headers: { "Content-Type": "application/json", Authorization: "Bearer " + tok }, body: JSON.stringify(body) }).then(async (r) => ({ status: r.status, j: await r.json() }));
let fails = 0; const check = (n, c) => { console.log((c ? "PASS " : "FAIL ") + n); if (!c) fails++; };
(async () => {
  const user = "verify_" + Date.now(), pw = "Zx9!kLm2#QwErT7";
  const A = await signIn(ADMIN_USER, ADMIN_PW); check("PC A: admin signs in", A.ok);
  const c = await api(A.j.idToken, { action: "create", username: user, password: pw, role: "User" });
  check("PC A: admin creates user in backend", c.status === 200);
  const B = await signIn(user, pw); check("PC B: same credentials accepted (fresh client)", B.ok);
  const B2 = await signIn(user, pw); check("PC B: login works again after 'restart'", B2.ok);
  check("Wrong password rejected", !(await signIn(user, pw + "x")).ok);
  check("Unknown user rejected", !(await signIn("nobody_" + Date.now(), pw)).ok);
  check("Duplicate username rejected (409)", (await api(A.j.idToken, { action: "create", username: user.toUpperCase(), password: pw, role: "User" })).status === 409);
  check("Non-admin cannot create accounts (403)", (await api(B.j.idToken, { action: "create", username: user + "x", password: pw, role: "User" })).status === 403);
  check("Missing token rejected (401)", (await api("bad", { action: "list" })).status === 401);
  await api(A.j.idToken, { action: "delete", username: user });
  check("Cleanup: test user deleted", !(await signIn(user, pw)).ok);
  console.log(fails ? `\n${fails} check(s) FAILED` : "\nAll checks passed."); process.exit(fails ? 1 : 0);
})().catch((e) => { console.error("Backend unreachable:", e.message); process.exit(2); });
