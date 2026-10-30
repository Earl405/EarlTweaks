# LiveBoost FPS Optimizer

1. Double-click BUILD-AND-RUN.bat (needs the .NET 8 SDK; the script installs it via winget if missing).
2. The app is built to LiveBoost-App\LiveBoost.exe. For FPS measuring and CPU temperature, run it as administrator.
3. Optional installer: install Inno Setup 6 and run BUILD-AND-RUN.bat again; installer\LiveBoost-Setup.exe is created from setup.iss.

The exe and installer use EARL_TWEAKS.ico (set via ApplicationIcon in LiveBoost.csproj and SetupIconFile in setup.iss).

Cross-PC accounts (1.5.1): accounts now live in Firebase Authentication, not on the PC. See ..\backend\SETUP.md to deploy the backend and fill in CloudConfig.cs.

What's new in 1.5: sign-in required at start. First run creates the administrator account (you choose the username/password);
the admin then creates users on the Accounts page (auto-generated username + strong password, copy, disable, reset, delete).
Everyone can change their own password in Settings and use Logout in the sidebar. Accounts are stored encrypted (Windows DPAPI) with
salted PBKDF2 password hashes in %LocalAppData%\LiveBoost\accounts.dat. This is basic local access control only.

What's new in 1.4: floating performance overlay (Ctrl+O), session recorder to CSV, manual priority control in Process Manager,
temperature/RAM alerts, optional Start with Windows, keyboard shortcuts (Ctrl+B boost, Ctrl+R restore), power plan and disk info.

What's new in 1.3: Advanced page (memory trim, temp cleaner, DNS flush, quick links, gaming-tweak status); new boost options
(Game Mode on, background game recording off, high-performance GPU for the game, memory trim); auto-boost now restores when the game closes.

What's new in 1.2: optional auto-boost when a game starts, remembers window size/position, filled graphs with overload colors,
process filter box, copy stream settings button, crash and log-size safety.

What's new in 1.1: custom icon, highlighted current page, admin/boost badges in the sidebar, nicer buttons, single-instance guard,
optional "restore on exit", benchmark export/clear, more auto-detected games.

Data (settings, profiles, restore state, log, benchmarks) is stored in %LocalAppData%\LiveBoost.
