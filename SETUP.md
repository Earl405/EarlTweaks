# EARL TWEAKS cross-PC accounts: free setup (no Blaze plan, no card)

Login = Firebase Authentication (free Spark plan). Admin actions = Cloudflare Worker (free plan) in `cloudflare-worker/worker.js`.
(`functions/` is the alternative Firebase-Functions backend; it needs Blaze. Ignore it.)

## 1. Firebase (free)
Authentication > Sign-in method > enable **Email/Password**. Copy Web API key + Project ID into `LiveBoost/CloudConfig.cs`.

## 2. Service-account key (kept secret, never in the app)
Project settings > Service accounts > **Generate new private key**. Keep the .json private.

## 3. Cloudflare Worker
1. Sign up free at dash.cloudflare.com > **Workers & Pages > Create > Create Worker**, name it `earl-tweaks-admin`, Deploy.
2. **Edit code**, delete the sample, paste ALL of `cloudflare-worker/worker.js`, **Deploy**.
3. Worker > **Settings > Variables and Secrets** > add three, type **Secret**:
   - `SERVICE_ACCOUNT_JSON` = the full contents of the key .json
   - `API_KEY` = your Firebase Web API key
   - `PROJECT_ID` = `earltweaks`
   Deploy again.
4. Copy the worker URL (`https://earl-tweaks-admin.<you>.workers.dev`) into `AdminApiBase` in `CloudConfig.cs`. Rebuild the app.

## 4. First admin (once, on your PC)
```
cd backend\functions && npm install
set GOOGLE_APPLICATION_CREDENTIALS=C:\path\key.json
cd ..\tools && node bootstrap-admin.js <adminUser> <strongPassword>
```

## 5. Verify
`node backend/tools/verify-cross-pc.js <webApiKey> <workerUrl> <adminUser> <adminPassword>`
then the manual two-PC check: create a user on PC A, log in on PC B, restart, log in again.
