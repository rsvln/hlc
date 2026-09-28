# Changelog

## 2.0.x

### New
- **Backups:**
  - scheduled runs (cron from `schedule`);
  - `maxParallelBackups` limit; a second run of the same host while one is in progress is skipped;
  - run history with logs (`config/backup-history.json`, shown in the UI), one line per run in `logPath`;
  - `retentionDays`: only `yyyyMMdd_HHmmss` directories are removed, the newest one is always kept.
- **Access:**
  - a user can be limited to a list of hosts;
  - audit log (`config/audit.log`, Config → Audit);
  - `HLC_BEHIND_PROXY=true` for running behind a reverse proxy.
- **Integrations:**
  - agent `/metrics` for Prometheus;
  - background monitoring of agents;
  - host page with a temperature chart;
  - notifications to Telegram and/or MQTT: agent offline / online, SMART degradation, a fan profile that lost its sensor, backup result;
  - MQTT discovery for Home Assistant: sensors, fans, disks, WoL / shutdown / reboot buttons.
- **Fan profiles — a lost sensor no longer goes unnoticed.** After the LibreHardwareMonitor update the disk sensor ids changed, and profiles silently fell back to fail-safe 100%:
  - agent: `GET /api/profiles/status` — `ok` / `sensorMissing` / `failsafe` / `fanMissing`; one log line per state change instead of a warning every 2 s;
  - Fan Control: status "Sensor missing — fan 100%" / "Fan missing" instead of "Active"; a missing sensor or fan is marked in the table and in the Edit dialog ("⚠ … (not found)" — the field used to be empty);
  - host page: a warning about such profiles;
  - `fanProfile` notification: a profile lost its sensor or fan, and works again.
- **Agent:**
  - a delayed shutdown survives an agent restart (within the same OS boot);
  - `force=false` for a graceful Windows shutdown;
  - fan profiles re-apply the speed periodically;
  - Swagger: the page opens without a key, methods are called with the key (**Authorize**); texts in English.
- **One API key per agent**, used by both HomeLabControl and Home Assistant. There is no separate Home Assistant key anymore: `haApiKey` in the config is still read and disappears on the next save. Existing `homeassistant` keys on agents keep working; new ones are not created.
- **HomeLabControl without Docker:**
  - self-contained win-x64 / linux-x64 builds with the agent builds inside;
  - Windows service / systemd, `install.cmd` / `install.sh`, `homelabcontrol.service` unit file;
  - port and administrator are set in `appsettings.Local.json` (same keys as in docker-compose);
  - the GitHub release carries the archives.
- **Manual agent install:**
  - every agent build contains `install.cmd` / `install.ps1` (Windows) and `install.sh` (Linux): service, API key, firewall rule, update that keeps the key, `-Uninstall`;
  - the scripts find an installed agent by its service and update it in place; a new install asks for the path and port (default `D:\apps\…` when there is a D: drive, else `C:\apps\…`; `/srv/…` on Linux);
  - an agent that ran without a key gets one only with `-GenerateKeys` / `--generate-keys`;
  - Config → Agents → **Add existing** registers a hand-installed agent by IP and key;
  - leaner build: no de-DE / es-ES and other locales, `appsettings.Development.json`, `web.config`, `*.pdb`.
- **Deploy:**
  - **Update all** goes on after a failed host and shows a summary at the end;
  - input validation;
  - automatic rollback when an updated agent does not start.
- **Documentation:** backups are described as a generic mechanism (configs first): push / pull modes, placeholders per mode, a "Linux configs" template in the example config.
- Favicon.
- Curve and custom-formula tests in CI; LibreHardwareMonitorLib 0.9.7-pre744.

### Fixed
- **Linux deploy:** the systemd unit file could get CRLF (`WorkingDirectory=/path\r`), and the service did not start.
- **Backups:** SCP with a quoted remote path; keys and paths in commands are escaped.
- **Backups:** the OpenWrt push template created the target directory on the router instead of the storage.
- **Fan profiles:** a custom-formula profile sent `null` instead of an empty point list.

## 2.0.0 — HomeLabControl and HomeLabControlAgent (2026-09-25)

HomeLabControl, HomeLabControlAgent and ShutdownDialog merged into one repository (`hlc/`). Moved to .NET 10.

### New
- **Sign-in and permissions in HomeLabControl:**
  - cookie sessions; the administrator comes from `HLC_ADMIN_USER` / `HLC_ADMIN_PASSWORD` in docker-compose and is created, or restored when needed, on start (without them — the `/setup` page);
  - users are stored in `config/users.yaml` (PBKDF2), managed on Config → Users; everyone changes their own password on Account;
  - per-module permissions: power / fanControl / backup — `view` or `control`, smart — `view`; `admin` gets everything, including agents and users;
  - permissions are checked both in the UI and on the server (handlers, REST);
  - brute-force protection, sessions are dropped when permissions or the password change, DataProtection keys are kept in the volume.
- **Agent authentication by API key:**
  - keys per client: `X-Api-Key` or `Bearer`;
  - stored in `appsettings.Local.json`, which a deploy never overwrites;
  - generated with `--generate-key`;
  - with no keys configured the API is open, and a warning is logged.
- **`GET /api/agent/info`:** version, OS, uptime, authentication state.
- **Single `config/HomeLabControl.yaml`:**
  - hosts with module sections `agent / power / fanControl / smart / backup`;
  - automatic migration of the three old files;
  - **Config** page with a YAML editor and validation before saving.
- **Agents grid** (Config → Agents):
  - version, health (online / open / key rejected / legacy / offline) and uptime;
  - actions: Deploy, Update, Update all, Remove (optionally uninstalling on the host).
- **Per-host API keys**, generated on deploy and kept by later deploys (a separate Home Assistant key at that time; merged into one key in 2.0.x).
- **HomeLabControl SSH key:** after the first deploy with a password, updates need no password.
- **Agent builds inside the HLC image** (self-contained, `InvariantGlobalization`): hosts need no .NET, a deploy uploads a single `tar.gz`.
- **Versioning:** `version.txt` + `Directory.Build.props` + `scripts/bump-version.ps1` (the version grows only when the sources change).

### Fixed — agent
- **Linux:** an immediate shutdown ran `systemctl poweroff --force --force` (no service stop, no unmount). Now it is a normal shutdown.
- **Linux:** a delayed shutdown used `systemctl --when` (missing before systemd 254, i.e. in Debian 12) and rounded the delay to minutes. `cancel` called `systemctl cancel`, which does not cancel a scheduled shutdown.
- **Windows:** `delay` ran as `Task.Delay` inside the HTTP request. The request hung, HLC timed out after 2 s, and the shutdown could not be cancelled.
- **Delayed actions** are now scheduled inside the agent on both OSes and cancelled with `/api/power/cancel`, which also closes ShutdownDialog.
- `Win32Shutdown` reported success on a non-zero return code.
- `WTSGetActiveConsoleSessionId` was compared with `0xFFFFFFFF` as an `int`, so the check never fired.
- **Custom curves always gave 0%:** the `Points.Count == 0` check came before the Custom branch. Any calculation error stopped the fan. Fail-safe is now 100%.
- **Lost sensor:** the fan stayed at its last speed. Now it goes to 100% after 3 cycles.
- **Back to BIOS control:** when a profile is disabled or deleted, and when the agent stops, control goes back to the BIOS / chip. On Linux `pwm_enable` used to stay in manual mode forever.
- **Profiles:** `profiles.json` was re-read from disk every 500 ms. Now there is an in-memory cache and atomic writes.
- **Deploy wiped profiles:** `profiles.json` was part of the publish output, and every deploy replaced it with an empty list.
- **Linux, disks:** `smartctl -A` ran on every sensor request (several times a second) and woke sleeping HDDs. Now the data is cached for 60 s, and sleeping disks are skipped with `-n standby`.
- **Linux, NVIDIA:** `nvidia-smi` ran twice a second and threw every time without a driver.
- **Linux, hwmon:** devices with the same name (`nvme`, `drivetemp`) overwrote each other. A fan id with `_` in the device name (e.g. `asus_wmi_sensors`) was not parsed.
- **Windows:** `GetFan` did not find the IT8613E Control sensor, so `/auto` answered "not supported", and a profile re-set the speed every cycle. Fan ↔ Control are now matched within one chip. LibreHardwareMonitor `Update()` is serialized.
- **Windows:** `/api/fans/{id}/auto` works (`SetDefault`).
- **External tools:** `smartctl` / `lsblk` ran with unread stderr (possible deadlock) and without timeouts.

### Fixed — HomeLabControl
- Cancelling a shutdown called the non-existent `/api/power/cancel-shutdown` and always failed. The agent now accepts both paths.
- In `appsettings.json` the key `AppСonfigPath` was typed with a Cyrillic "С" and had a space in the value, so the setting was ignored.
- `{{DATE}}` in backups was computed per command: `mkdir` and `scp` could get different directories.
- The backup SSH key name allowed path traversal (`../../`).
- REST `/api/powercontrol/*` accepted any `ip:port` (a proxy to anywhere in the network). Now only hosts from the config.
- Deploy from Fan Control appended the server to a hard-coded `config/FanControl.yaml` and created duplicates.
- Hosts in `PowerControl.yaml` kept the ports of the old services (`5000`, `8116`).
- Removed dead WinRM / PowerShell code and the `System.Management.Automation`, `Renci.SshNet.Async` packages. SSH.NET updated to 2026.0.0: 2025.1.0 had known vulnerabilities.
- .NET 10: `blazor.server.js` became a static web asset (`UseStaticWebAssets`).
- YAML editor on the Config page: Monaco (BootstrapBlazor.CodeEditor) did not start in Firefox. Replaced with CodeMirror 6 (source in `HomeLabControl/webui/` → `wwwroot/js/yaml-editor.js`), loaded with the version in the URL; a plain textarea if it fails to load. Static files are served with `Cache-Control: no-cache`.
