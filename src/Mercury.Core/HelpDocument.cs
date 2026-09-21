namespace Mercury;

public sealed class HelpSection
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
}

public static class HelpDocument
{
    public static IReadOnlyList<HelpSection> Sections { get; } =
    [
        new()
        {
            Id = "start",
            Title = "Mercury",
            Body =
                "Mercury copies files from any Explorer path to a folder, USB, UNC share, or a Catcher over HTTPS. " +
                "Jobs can pause, resume, and verify that destination files match. There is no installer.\n\n" +
                "Data defaults to %APPDATA%\\Mercury so journals survive swapping the exe. Open the Settings tab for every file Mercury writes. " +
                "Use portable mode (beside the exe) only if you want a USB toolkit copy.\n\n" +
                "This Help tab is searchable. Sections: Transfer, Job options, RoboFlags, Progress, Tree, Queue, Console, History, Network, Catcher setup, Theme, Settings."
        },
        new()
        {
            Id = "transfer",
            Title = "Transfer tab",
            Body =
                "Source and Destination accept browse, paste, or drag-drop. Destination can be a folder or Catcher (HTTPS). " +
                "Browse Source remembers the last Source folder; Browse Destination remembers the last Destination. Recents stay independent.\n\n" +
                "A selected folder copies as dest\\FolderName (Explorer-style) — Include source folder name is on by default. " +
                "Pick the parent destination; Will land in under Destination shows the full path before Start (D:\\Anchor Span → dest\\Anchor Span). " +
                "Uncheck it only for contents-only (robocopy-style) so children such as compressed land directly in dest. " +
                "Drive roots always dump contents into dest (no extra D). Dest already containing a child folder does not uncheck wrap. " +
                "Browsing Destination inside a previous copy will nest FolderName again.\n\n" +
                "Start runs the job now. If Source/Dest match a stopped, incomplete, or queued job, Start resumes that same job id instead of creating a duplicate. Add to queue stores a new Pending row and does not start it. Resume last continues the journal of the last job (including deferred files). " +
                "Before resume, Mercury asks whether to check the source for changes first (Yes = walk vs journal, cancellable; No = journal as-is).\n\n" +
                "Pause stops between chunks of the current file (temp .mercury.tmp stays until you Resume). " +
                "Stop or Close now also keeps .mercury.tmp for large files so Resume last continues from the last written offset instead of recopying from byte 0. " +
                "Pause after this file finishes the file in flight, then pauses — it will not cut the temp mid-stream. " +
                "The button becomes Remove Pause after while waiting; click it to cancel. " +
                "If the remaining file will take 10 seconds or more, Mercury shows that wait when you click it, and the Progress header shows a live Pause after this file: ~Xm line.\n\n" +
                "Stop cancels and keeps progress for Resume last (this is a clean stop — next launch will not prompt). " +
                "Close (X / Alt+F4) while a job is copying asks: Wait for this file, Close now, or Cancel. " +
                "Wait for this file uses Pause after this file, then exits. Close now keeps the in-flight .mercury.tmp (offset resume) and leaves a dirty heartbeat so the next launch offers resume. " +
                "A crash, kill, or reboot leaves the same dirty flag.\n\n" +
                "Stop cancels and keeps progress for Resume. Pause all / Resume all apply to every running job."
        },
        new()
        {
            Id = "options",
            Title = "Job options",
            Body =
                "Job options order: Start after + Window, then Overwrite + Verify, then Unlimited / Max / Dry Run / Small Files / Skip compressed / Ignore Storage Limit / RoboFlags / Retries / Wait.\n\n" +
                "Max throttles this job. The unit (MB/s or Mbps) sits to the right of the box and follows Global bandwidth → Show speeds in Mbps (1 MB/s = 8 Mbps). Stored caps stay MB/s.\n\n" +
                "Global bandwidth Min / Unlimited / Max / Throttle active PC apply to all jobs immediately. Each number box has its unit on the right. Show speeds in Mbps also switches Progress Speed.\n\n" +
                "Throttle active PC: while other programs are using the PC " +
                "(CPU above about 18%, excluding Mercury’s own copy when possible), every job is capped at that speed. " +
                "When the PC is idle, Unlimited / Max still apply. The cap takes effect mid-job — no restart.\n\n" +
                "Window pauses copying outside the daily hours (status: Paused — outside hours).\n\n" +
                "Retries / Wait: immediate retries on a locked file. If those fail with a sharing/lock error, Mercury defers the file and retries at 25%, 50%, 75%, and 100% of bytes copied (or at the end of a small job) so you can close the file.\n\n" +
                "Overwrite: skip if dest is newer or equal (2-second FAT tolerance only when RoboFlags → FAT 2s times is on), always, or never if dest exists. The combo is sized to the longest choice.\n\n" +
                "Verify Quick checks size + timestamp. Thorough also hashes (xxHash64).\n\n" +
                "Dry Run enumerates only.\n\n" +
                "Small Files writes one uncompressed transport zip (fast on USB / many small files), unpacks a normal folder tree at dest, then deletes the zip. " +
                "Skip compressed (default on with Small Files) copies video, photos, audio, zip/7z/rar, ISO, and similar as files — they are not wrapped in that zip. " +
                "If the whole job is already compressed, packing is skipped entirely (no zip-of-zips). Leave Small Files off to copy file-by-file. Large as-is files resume from .mercury.tmp offset after Stop.\n\n" +
                "Ignore Storage Limit: still logs Need vs free space but does not abort on thin/growable volumes.\n\n" +
                "RoboFlags (button on this tab) opens native copy checkboxes — see the RoboFlags help section. Timestamps default on so dest keeps source creation time.\n\n" +
                "Include source folder name (default on): the selected top-level folder is created at dest. Uncheck for Contents only.\n\n" +
                "Start after: calendar start; Window hours still apply if enabled."
        },
        new()
        {
            Id = "roboflags",
            Title = "RoboFlags",
            Body =
                "On Transfer → Job options, RoboFlags expands checkboxes for the native .NET copier. Mercury does not run robocopy.exe. " +
                "Flags persist on the job, saved jobs, and the queue. Hover a box for the robocopy cousin.\n\n" +
                "Timestamps — /COPY:T — source creation time + last-write (default on). Windows copy otherwise sets dest CreationTime to now.\n" +
                "Attributes — /COPY:A — read-only, hidden, archive, and similar (default on).\n" +
                "NTFS ACL / security — /COPY:S / /SEC — DACL (default off; fail-soft + log).\n" +
                "Owner — /COPY:O — owner (default off; may need elevation; fail-soft + log).\n" +
                "Directory timestamps — /DCOPY:T — folder creation + last-write (default on).\n" +
                "Empty directories — /E vs /S — create empty source folders (default on).\n" +
                "Unbuffered I/O — /J — Force unbuffered (write-through, sector-aligned). Default off = auto-probe. " +
                "When unchecked, Mercury enumerates types (video, ISO, VHD, …) and, if there is large sequential payload, copies a short buffered sample then an unbuffered sample and keeps the faster mode. " +
                "Small files stay buffered. Probe is skipped if you force unbuffered, if there is no large sequential payload, or if a speed cap (Max MB/s or Throttle active PC) is already limiting the job. " +
                "Unbuffered tends to help large sequential files on HDD/USB and already-compressed blobs; it hurts thousands of tiny files.\n" +
                "Copy symbolic links as links — /SL — copy links as links (default off: skip reparse, do not follow).\n" +
                "FAT 2s times — /FFT — 2-second compare for skip/verify (default off; turn on for FAT dest).\n" +
                "Exclude hidden/system — /XA:HS — skip hidden and system files/folders (default off).\n" +
                "Purge extra dest files — /PURGE — delete dest extras (default off; extra confirm; destructive).\n\n" +
                "After each file (and after pack-as-zip unpack), Mercury sets File.SetCreationTimeUtc / SetLastWriteTimeUtc and attributes from the source when those boxes are on."
        },
        new()
        {
            Id = "progress",
            Title = "Progress header",
            Body =
                "The pink header is Progress. Current and Overall bars stay on one row for running, paused, stopped, and finished (Overall hides only when there is a single job). Each bar shows its percent in the middle. " +
                "Below 1%, the label uses one decimal (0.1%) or <1% so a started job never shows 0%. " +
                "The stats table is always Stage / File / Elapsed / This stage / Files / Bytes / Speed / ETA — inactive cells show — (or last known values), not a different rundown-only layout. Exception text never replaces the table; errors go to Console and History. " +
                "Show speeds in Mbps (Global bandwidth) switches Speed between MB/s and Mbps (1 MB/s = 8 Mbps). " +
                "Job n of m is which queue item (example: 3 of 3). Stage is that job’s pipeline step: enumerate, copy, verify, writing rundown. " +
                "With no running or resumable job the header stays collapsed — no 0/0 files or empty rundown. A part-way last job still shows its last percent and counts so you can Resume last. " +
                "When two or more jobs are in the queue, the stats table includes Overall files (example: Files: 12/400). " +
                "Job: 2 of 5 is the running job’s place in the listed queue (omitted when there is a single job). " +
                "Stats sit under the bars in a table (key: value), including the current File name. Status text lives in this header — the Transfer tab no longer repeats them. " +
                "During enumerate, Types shows a mix such as Video: 40 files, 2.1 TB. " +
                "Writing rundown runs in the background after copy and verify so the next queued job can start transferring. " +
                "The copier stays one job at a time; Start is enabled when that copy slot is free. Stop during rundown is immediate."
        },
        new()
        {
            Id = "tree",
            Title = "Tree tab",
            Body =
                "Folders only from the current or last job journal — not 125k file nodes. Each row: folder name, Files, Subdirs, Done %, ETA. " +
                "Expand a folder for the same columns on child folders. Leaf folders show Subdirs as 0. Starts collapsed at the source’s immediate child folders. " +
                "Done % is copied/unpacked/skipped bytes in that subtree versus the enumerated total there (file count if sizes are 0). " +
                "ETA is remaining subtree bytes ÷ job speed, or — until speed exists. Idle with no job is empty."
        },
        new()
        {
            Id = "queue",
            Title = "Queue tab",
            Body =
                "Jobs wait here until you Start them, or until the previous job finishes (queue drain). Add to queue never auto-starts. " +
                "An empty queue shows one line: No jobs in the queue. Each job is a tile: order #, source → dest, status (Pending / Running / Paused / On hold / Stopped / Done), flag badges, and per-tile Start / Pause / Stop (plus Hold, Job Options, Remove, Up, Down). " +
                "Job Options on a tile pops out the same fields as Transfer (speed, Window, retries, overwrite, verify, Dry Run, Small Files, Skip compressed, Ignore Storage Limit, Start after, RoboFlags, Include source folder name, Catcher template). " +
                "Those values are stored on the job you Add — they are not locked to the Transfer tab while a copy runs. " +
                "Tiles show compact badges for non-default flags (Dry Run, Small Files, Ignore Storage Limit, Window, Hold, Catcher, Contents only when wrap is off). " +
                "Source/Dest/Browse/Add on this tab stay enabled while a transfer is running — only Transfer-tab paths lock with the active job. " +
                "One copy runs at a time, in listed order: after a job finishes, the first not-on-hold Pending job that is due starts next " +
                "(a later job will not jump a not-due job ahead of it). Writing rundown for a finished job can continue in the background while the next copy starts. " +
                "Hold marks a queued job On hold so it never auto-starts; it stays until you Unhold or Start/Resume that row. " +
                "Pause all affects every active job."
        },
        new()
        {
            Id = "console",
            Title = "Console tab",
            Body =
                "Live log of the current session. Search filters lines; Follow stays on the newest line; Errors only hides info. " +
                "Copy copies the view. Open logs folder opens the logs directory. " +
                "A daily error log (mercury-YYYYMMDD.log) is also written under Settings so you can check failures later. " +
                "When a finished job is writing its rundown off the copy slot, the console logs Rundown running in background."
        },
        new()
        {
            Id = "history",
            Title = "History tab",
            Body =
                "Finished transfers, newest first. Click a row to restore its rundown (started, ended, files, mismatch). Stored in history.db."
        },
        new()
        {
            Id = "network",
            Title = "Network tab",
            Body =
                "Create a Catcher template (public host/IP, public port, internal listen port, optional bind IP). " +
                "Export a .mercury-catch file — the passphrase is not stored in the file. Copy that file to the receiving PC. " +
                "Default public port is 443; Catcher listens on 8443 internally. Mercury does not punch NAT: the router must forward 443 → the Catcher LAN port, and Catcher must already be listening."
        },
        new()
        {
            Id = "catcher",
            Title = "Catcher setup",
            Body =
                "On the receiving PC run MercuryCatcher.exe. Import the .mercury-catch file, enter the same passphrase (required, at least 8 characters), pick a receive folder, Listen, then tick Ready to receive.\n\n" +
                "Status: Offline (no HTTPS), Listening (up but not accepting), Ready (accepting). The sender polls /v1/ready with the template passphrase and pinned TLS fingerprint — never host:port alone.\n\n" +
                "Wrong fingerprint = refuse (possible MITM). Wrong or empty passphrase = unauthorized. " +
                "A different Catcher’s file will not unwrap with this passphrase, and its certificate will not match the pin.\n\n" +
                "v1 sends one zip pack (or a single file). Catcher unpacks the zip into a folder tree and deletes the transport zip. Per-file HTTPS resume is not in this version. TLS cannot be turned off."
        },
        new()
        {
            Id = "theme",
            Title = "Theme tab",
            Body =
                "Colours use hex or the picker and apply live. Save / Load / Delete named themes in themes.json. " +
                "Export writes a portable .mercury-theme.json (colours and fonts) you can attach in chat or Import later. " +
                "Fonts are per text type: progress stats, labels/keys, values, body/tabs, buttons, console. " +
                "Hover a colour or font row while the Theme tab (or preview) is open to outline the chrome that uses it. " +
                "Leaving Theme clears those outlines and closes the preview. " +
                "Hover or click an element in the theme preview to highlight the matching editor rows. Reset restores the shipping default."
        },
        new()
        {
            Id = "settings",
            Title = "Settings tab",
            Body =
                "Lists every path Mercury writes (data folder, error log, job logs, journals, settings.json, last-job.json, queue.json, history.db, themes.json, Catcher files). " +
                "Open folder jumps to that location in Explorer.\n\n" +
                "Default data root is %APPDATA%\\Mercury. Portable (beside the exe) is off unless you tick it (takes effect on next launch) " +
                "or Mercury finds jobs beside the exe and none in AppData — it will keep that journal so Resume last still works."
        }
    ];

    public static IEnumerable<HelpSection> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Sections;
        }

        return Sections.Where(s =>
            s.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            s.Body.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
}
