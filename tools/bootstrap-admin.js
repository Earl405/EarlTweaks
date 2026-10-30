// Run ONCE by the owner, on the owner's own machine, to create the first Administrator.
// Needs a service-account key (Firebase console > Project settings > Service accounts). The key stays on YOUR
// machine and is never put in the app. Usage:
//   set GOOGLE_APPLICATION_CREDENTIALS=C:\path\serviceAccount.json
//   node bootstrap-admin.js <adminUsername> <adminPassword>
const admin = require("../functions/node_modules/firebase-admin");
admin.initializeApp();
const [name, pw] = process.argv.slice(2);
if (!name || !pw) { console.error("Usage: node bootstrap-admin.js <username> <password>"); process.exit(1); }
if (!/^[A-Za-z0-9_.-]{3,32}$/.test(name) || pw.length < 12) { console.error("Invalid username or password (12+ chars)."); process.exit(1); }
(async () => {
  const email = `${name.toLowerCase()}@earltweaks.example`;
  let u;
  try { u = await admin.auth().getUserByEmail(email); await admin.auth().updateUser(u.uid, { password: pw, disabled: false }); }
  catch (e) { if (e.code !== "auth/user-not-found") throw e; u = await admin.auth().createUser({ email, password: pw, displayName: name }); }
  await admin.auth().setCustomUserClaims(u.uid, { role: "Admin" });
  console.log("Administrator ready:", name);
})().catch((e) => { console.error(e.message); process.exit(1); });
