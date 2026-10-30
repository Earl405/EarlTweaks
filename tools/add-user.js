// Create (or reset) a regular User, or an Admin, from the command line. Run on YOUR PC only (uses the service-account key).
//   set GOOGLE_APPLICATION_CREDENTIALS=C:\path\key.json
//   node add-user.js <username> <password> [User|Admin]
const admin = require("../functions/node_modules/firebase-admin");
admin.initializeApp();
const [name, pw, roleArg] = process.argv.slice(2);
const role = (roleArg || "User").toLowerCase() === "admin" ? "Admin" : "User";
const kinds = [/[a-z]/, /[A-Z]/, /[0-9]/, /[^A-Za-z0-9]/].filter((r) => r.test(pw || "")).length;
if (!name || !pw) { console.error("Usage: node add-user.js <username> <password> [User|Admin]"); process.exit(1); }
if (!/^[A-Za-z0-9_.-]{3,32}$/.test(name)) { console.error("Username: 3-32 characters, letters/numbers/_ . - only (no @, no spaces)."); process.exit(1); }
if (pw.length < 12 || pw.length > 128 || kinds < 3) { console.error("Password: 12+ characters with at least 3 kinds (lower, upper, numbers, symbols)."); process.exit(1); }
(async () => {
  const email = `${name.toLowerCase()}@earltweaks.example`;
  let u, created = false;
  try { u = await admin.auth().getUserByEmail(email); await admin.auth().updateUser(u.uid, { password: pw, disabled: false }); }
  catch (e) { if (e.code !== "auth/user-not-found") throw e; u = await admin.auth().createUser({ email, password: pw, displayName: name }); created = true; }
  await admin.auth().setCustomUserClaims(u.uid, { role });
  console.log(`${created ? "Created" : "Updated"} ${role}: ${name}`);
})().catch((e) => { console.error(e.message); process.exit(1); });
