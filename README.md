# Le Robocopier

A C#/.NET 10 and Avalonia desktop application for organizing backups by project, with configuration persisted in XML.

Implemented features:

- Create projects using New project, with automatic saving.
- Active project cards with single selection.
- Open project editor with native file and folder pickers, item types and full paths.
- Add and remove items with automatic saving; removal preserves original files and backups.
- Reject duplicate items, overlapping folders and selections that match, contain or are inside the backup root.
- Settings panel showing the backup root and confirmation preferences.
- Validate empty, invalid or duplicate names, including names reserved by inactive projects.
- Preserve the previous configuration on write failures and disable changes when an existing XML file is unreadable.
- Copy the selected project's current contents, including hidden files and empty folders, while preserving originals and extra backup files.
- Restore configured items from backup to their registered original paths, creating parents and preserving the backup and extra original files.
- Preview Restore overwrites and warn about selected items missing from the backup; removed items are excluded.
- Move selected items into the backup, removing originals only after every copy passes size and SHA-256 verification.
- Detect source changes and new folder contents before removal, delete only inventoried files and empty folders, and report removed and remaining paths on partial failure.
- Keep full source paths under each project's backup folder, separating drive letters, UNC shares and the Linux root.
- Check paths and links before copying, then verify copied files by size and streaming SHA-256.
- Show preparation, copying, verification, metadata and removal progress with warnings, failures and partial effects in a modal dialog.
- Persist independent Copy, Move and Restore confirmation preferences and re-enable each in Settings.
- Prevent concurrent operations, registry changes during execution and another instance using the same configuration.

The backup root is displayed for reference. During Copy, Move and Restore, the operation dialog and main window cannot be closed normally. There is no cancellation while running.

A folder is registered as a single item: its contents are determined at the time of the operation. Returning from the editor keeps the project selected.

Configuration location: `%LOCALAPPDATA%\LeRobocopier\settings.xml` on Windows; `$XDG_CONFIG_HOME/LeRobocopier/settings.xml` on Linux, falling back to `~/.config` when XDG is missing, empty or relative. The default backup root is `LeRobocopier/Backups` inside the user's home folder. The XML file is created when a project or confirmation preference is first saved. An unreadable file must be corrected before restarting the application. Existing version-1 XML names are preserved for compatibility.

Copy and Restore preserve modification times, common file attributes and Linux permissions, including executable bits. They do not replicate ownership or ACLs. Both revalidate the inventory before writing; when Restore confirmation is enabled, a new overwrite requires preparing again. External programs can still change files; reported failures do not roll back completed replacements. Linux file-type validation uses `statx` to reject special files as well as symbolic links.

Move uses the same copying and metadata policies. It freezes source hashes in the execution inventory, checks all source and backup files again before the first removal, and revalidates paths, content and inventoried folder membership before each deletion. New content is preserved; folders are removed only when empty, without recursive deletion. Copy or verification failures preserve all originals. Removal failures keep backup copies and report paths removed and not removed, including newly detected content. The verification list records completed checks; check any reported verification failure before using a backup. External writers and system interruptions are not covered by an atomic transaction, and removals are not rolled back.
