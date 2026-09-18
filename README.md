# Mercury

Portable Windows file transfer: speed limits, hours of operation, restartable jobs, and a completeness check at the end. No installer.

Copy any Explorer path (file, folder, or whole drive) to any writable destination (local disk, USB, mapped drive, UNC, cloud-synced folder).

## Run (no install)

1. Copy the `Mercury` folder onto the PC or a USB toolkit drive.
2. Double-click `Mercury.exe` to send, or `MercuryCatcher.exe` to receive over HTTPS.
3. Pick source and destination (browse, paste, or drop from Explorer).
4. Set speed / hours / verify if you want, then **Start** or **Add to queue**.

Speed limits (per-job Max MB/s, global min/max, and **Throttle to X MB/s when not idle**) apply immediately while a job is running — no restart. Other in-flight option changes (hours, overwrite, verify, retries, dest) are a later pass.

**Many small files / USB hard drive:** turn on **Pack as zip**. Mercury writes one stored (uncompressed) `.zip` for the transfer (one sequential write — much faster on USB HDDs), then **unpacks it at the destination** into a normal folder tree and deletes the zip. Do not open the zip in Explorer — Mercury unpacks it in place (including on a network dest). Leave the option off to copy files one-by-one.

There is no setup, no admin prompt, and no .NET SDK required on the machine that runs it. The published folder includes the runtime.

### Where data lives

On first run Mercury uses `%APPDATA%\Mercury` (Roaming) so logs and journals survive swapping the exe:

```text
%APPDATA%\Mercury\
  logs\
    mercury-YYYYMMDD.log          error log (also Catcher: mercury-catcher-YYYYMMDD.log)
    job-{id}.log
  jobs\{id}\job.db                resume journal
  settings.json
  last-job.json
  queue.json
  recents.json
  saved-jobs.json
  history.db
  themes.json
  catcher-templates.json
  catcher-settings.json
```

Tick **Portable: keep data beside Mercury.exe** on the Settings tab if you want a USB toolkit copy (`Mercury\data\`). If AppData has no jobs and `data\` beside the exe already has journals, Mercury keeps using the beside-exe folder so Resume last still works. The Settings tab lists every path with an Open folder button.

The status bar shows the data folder. Console → Open logs folder, or Settings → Error log.

Closing the window while a copy is running asks whether to wait for the current file or close now. A crash, kill, or reboot leaves a dirty heartbeat (`jobs\{id}\heartbeat.json` plus journal meta); the next launch offers resume. Use **Resume last** for a clean Stop as well.

## Options (robocopy equivalents)

| Mercury | Robocopy |
|---|---|
| Max MB/s (per job) + global min/max + throttle when not idle | `/IPG` |
| Hours of operation | `/RH` |
| Resume last / journal | `/Z` |
| Recurse folders | `/E` |
| Retries + wait | `/R` `/W` |
| Skip if dest newer or equal | `/XO` (+ `/FFT` when RoboFlags → FAT 2s times is on) |
| Dry run | `/L` |
| Destination can expand (ignore free-space abort) | (thin / growable dest; still logs Need vs available) |
| Console tab + log files | `/LOG` `/TEE` |
| **RoboFlags** (native copier, not robocopy.exe) | |
| Timestamps (creation + last-write) — default on | `/COPY:T` |
| Attributes — default on | `/COPY:A` |
| NTFS ACL — default off | `/COPY:S` `/SEC` |
| Owner — default off | `/COPY:O` |
| Directory timestamps — default on | `/DCOPY:T` |
| Empty directories — default on | `/E` |
| Force unbuffered I/O (unchecked = auto-probe) | `/J` |
| Copy symbolic links as links — default off (skip reparse) | `/SL` |
| FAT 2s times — default off | `/FFT` |
| Exclude hidden/system — default off | `/XA:HS` |
| Purge extra dest files — default off (confirms) | `/PURGE` |

**Verify (after copy, not optional for “success”):**

- **Quick:** every source file exists at dest, sizes match, timestamps within 2 seconds.
- **Thorough:** also xxHash64 (hash-while-copy, then dest re-read).

Result is **Verified complete** or **Incomplete — N issues**.

## Cloud destinations

If the destination looks like OneDrive / SharePoint / similar, Mercury verifies the **local** copy. Upload into the cloud is the sync client’s job. The yellow notice on the Transfer tab is that caveat.

UNC / Azure Files / SMB is a real write; verify is against that share.

## Encrypted transfer (Catcher)

Folder / USB / UNC copy is unchanged. Catcher is an extra **network** path: the sender connects **outbound** to a Catcher that is already listening. Mercury does not punch through NAT by itself.

### Ports

| Setting | Default | Where it lives |
|---|---|---|
| Public host / IP | *(you type the WAN hostname or IP)* | Template |
| Public port | **443** | Router WAN — what the sender connects to |
| Internal listen port | **8443** | Catcher LAN bind (usually no admin) |
| Bind IP | blank = all interfaces (`0.0.0.0`) | Catcher. Loopback only works on the same PC. |

On the receiving router, forward **once**:

`publicIP:publicPort` → `catcher-LAN-IP:internalListenPort`

Example: `203.0.113.10:443` → `192.168.1.20:8443`. Catcher speaks **HTTPS** (TLS 1.3 preferred, TLS 1.2 accepted) on the internal port so firewalls see TLS, not a custom cleartext protocol.

### Settings template / file format

On **Network**, create a named template: scheme `https-tls1.3`, passphrase, public host, public port, internal port, optional bind IP. Create generates a self-signed certificate and shows its SHA-256 fingerprint. **Export** writes a `.mercury-catch` file (JSON envelope).

- Plaintext in the file: name, scheme, host, ports, bind, fingerprint, KDF salt, auth salt.
- Wrapped with the passphrase (PBKDF2-SHA256 + AES-256-GCM): the TLS private key. The passphrase itself is **not** stored in the file.
- Catcher **Import file** → pick receive folder → enter passphrase → **Listen**.
- Sender: Transfer destination **Catcher**, pick the same template, enter the passphrase, **Start**.

The sender pins the fingerprint. A mismatch is refused (possible MITM). There is no “disable TLS” option. Passphrases are not written to logs.

### How to run

1. **Sender PC:** Mercury → Network → create template (public host = the receiving site’s WAN IP or DNS name) → Export `*.mercury-catch`.
2. Copy that file to the receiving PC (USB/email — it is wrapped).
3. **Receiving PC:** double-click `MercuryCatcher.exe` → Import → receive folder → passphrase → Listen. Allow Windows Firewall if prompted.
4. **Router at the receiving site:** port-forward as above.
5. **Sender PC:** Transfer → destination Catcher → template + passphrase → Start.

Same-machine smoke test: public host `127.0.0.1`, public port and internal port both `8443`, bind `127.0.0.1`. That does not prove NAT.

v1 sends **one zip pack** for a folder (or the source file as-is if you pick a single file). Catcher unpacks that zip into a folder tree in the receive folder and deletes the transport zip. Progress is shown. Per-file journal resume over HTTPS is later — restart the send if it fails. Disk copy (USB/UNC) still has resume, including unpack if you Stop during Unpacking.

### Catcher vs sender

| | Mercury.exe | MercuryCatcher.exe |
|---|---|---|
| Role | Sender | Listener |
| Start | Transfer tab | Import + Listen |
| Connects | Outbound HTTPS to public host:port | Binds internal port |

Both live in the same portable folder. Catcher templates are stored in `data\catcher-templates.json` (still wrapped).

## Queue, history, and schedule

Jobs run **one at a time**, in **queue list order**. **Add to queue** captures the current Transfer settings (including optional **Start after** date/time) and persists them in `data\queue.json`. Only the first **not-on-hold** Pending job auto-starts; a later job will not jump a not-due job ahead of it. **Hold** keeps a row in the queue indefinitely (no auto-start) until you Unhold or Resume that row. The next job starts when the current one finishes **and** it is due: `now >= Start after` (if set) and inside hours of operation (if those are enabled).

**Browse Source** opens the last Source folder; **Browse Destination** opens the last Destination (`lastSourceDir` / `lastDestDir` in `recents.json`). Recents dropdowns stay independent.

**Start** ignores the calendar picker and runs as soon as the slot is free (hours still apply). Closing the app keeps queued jobs; interrupted copies show as stopped and can be resumed from the Queue tab.

The **History** tab lists finished transfers (newest first) from `data\history.db`. Click a row to restore that transfer’s rundown. The Progress header always shows **Current**; **Overall** (bar + overall file counts) appears under it when two or more jobs are queued. **Job: n of m** is the running job’s place in the list. While a copy is running, Current shows **Stage: n of m** and a live elapsed timer; a new Start clears the previous rundown immediately.

**Throttle when not idle** (Transfer → Global bandwidth): cap all jobs to X MB/s while other programs are using the PC (CPU above about 18%, excluding Mercury’s own copy when possible). When idle, Unlimited / Max MB/s apply. Lives in `settings.json`.

## v1 vs later

The queue is sequential (`MaxConcurrentJobs` stays 1). Raising concurrency is a later switch.

<!-- Phase 3: apply hours/overwrite/verify/retries/dest on the fly with warning popups for dangerous in-flight edits. Speed is already live. -->

## Build from source

Windows 10/11 x64, .NET 8 SDK (to compile only):

```powershell
dotnet test Mercury.slnx
dotnet build Mercury.slnx -c Release
dotnet run --project src\Mercury\Mercury.csproj
.\publish.ps1
```

`dotnet build -c Release` (solution or `src\Mercury\Mercury.csproj`) also publishes a **self-contained win-x64 folder** (not single-file) to `dist\Mercury\` including `MercuryCatcher.exe`. Debug `dotnet build` does not — keep that as the fast inner loop. Override: `-p:PublishPortable=true` or `-p:PublishPortable=false` (alias `-p:Portable=`).

`.\publish.ps1` writes the same folder (and can zip it). If `dist\Mercury\Mercury.exe` or `MercuryCatcher.exe` is running, the portable refresh is skipped; close that exe and rebuild — a copy job or listener is never killed.

Output: `dist\Mercury\` — copy that folder; do not install it.

### Assumptions

- Runtime: Windows 10/11 x64; .NET 8 is **bundled** in the published folder.
- Network: folder copy needs the same reachability Explorer already needs. Catcher needs the sender to reach `publicHost:publicPort`, a listening Catcher, and a one-time router port-forward. Windows Firewall must allow Catcher on the internal port.
- Permissions: user-level; writes next to the exe (`data\`) or LocalAppData. No admin. Binding port 443 on Catcher itself may need elevation — default internal port is 8443.
- Topology: folder copy is one PC. Catcher is sender outbound → receiving-site listener.

### Verification

- [x] Unit tests: throttle, hours window, resume skip, missing-file verify fail, copy shape, pack-as-zip unpack-at-dest, Catcher pack wrap/unwrap, Catcher HTTPS push of a small folder (unpacked tree), fingerprint pin, wrong passphrase.
- [x] After publish, `Mercury.exe` and `MercuryCatcher.exe` exist in `dist\Mercury\` (self-contained folder).
- [ ] On a PC without the .NET SDK: copy `dist\Mercury` and double-click (operator check).
- [ ] Across a real router: port-forward + Catcher listen + sender Start (operator check).
