# Le Robocopier

A C#/.NET 10 and Avalonia desktop application for organizing backups by project, with configuration persisted in XML.

Implemented features:

- Create projects using New project, with automatic saving.
- Active project cards with single selection.
- Deactivate a project after confirmation, preserving its identity, registered items and backups; reactivate it in Settings.
- Open project editor with native file and folder pickers, item types and full paths.
- Rename a project and its existing backup folder, preserving its identity, selection, registered items and active state; reject occupied destinations without merging.
- Add and remove items with automatic saving; removal preserves original files and backups.
- Reject duplicate items, overlapping folders and selections that match, contain or are inside the backup root.
- Settings panel showing the backup root and confirmation preferences.
- Change the backup root in Settings after a separate confirmation, migrating complete backups of active and inactive projects, including extra files and removed selections.
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

During Copy, Move, Restore and backup-root migration, the operation dialog and main window cannot be closed normally. There is no cancellation while running.

A folder is registered as a single item: its contents are determined at the time of the operation. Returning from the editor keeps the project selected.

Configuration location: `%LOCALAPPDATA%\LeRobocopier\settings.xml` on Windows; `$XDG_CONFIG_HOME/LeRobocopier/settings.xml` on Linux, falling back to `~/.config` when XDG is missing, empty or relative. The default backup root is `LeRobocopier/Backups` inside the user's home folder. The XML file is created when a project or confirmation preference is first saved. An unreadable file must be corrected before restarting the application. Existing version-1 XML names are preserved for compatibility.

Rename moves an existing backup through a temporary sibling folder and records the pending change beside the configuration. After an interruption, startup uses the persisted project name to recover the folder. Ambiguous or inaccessible recovery blocks changes and reports the preserved candidate paths. A project without a backup is renamed without creating a backup folder. External filesystem changes and power loss are not covered by an atomic transaction.

Copy and Restore preserve modification times, common file attributes and Linux permissions, including executable bits. They do not replicate ownership or ACLs. Both revalidate the inventory before writing; when Restore confirmation is enabled, a new overwrite requires preparing again. External programs can still change files; reported failures do not roll back completed replacements. Linux file-type validation uses `statx` to reject special files as well as symbolic links.

Move uses the same copying and metadata policies. It freezes source hashes in the execution inventory, checks all source and backup files again before the first removal, and revalidates paths, content and inventoried folder membership before each deletion. New content is preserved; folders are removed only when empty, without recursive deletion. Copy or verification failures preserve all originals. Removal failures keep backup copies and report paths removed and not removed, including newly detected content. The verification list records completed checks; check any reported verification failure before using a backup. External writers and system interruptions are not covered by an atomic transaction, and removals are not rolled back.

Backup-root migration rejects occupied project destinations, roots that contain each other, and overlaps with registered items, including inactive projects. Selecting the current root does nothing. Missing backups are skipped. Copies use an identified staging folder, then publish without merging occupied folders and pass global SHA-256 verification before the new root is saved. Only then are inventoried old backup files and empty folders removed; unrelated contents in both roots remain. A transfer or save failure preserves old backups and reports paths already written, which may require manual inspection before retrying. If saving completed before reporting an error, the application reloads the saved root and still skips old-backup cleanup. An inconclusive reload blocks changes until restart. Cleanup failures keep the new root active and list remaining old paths. Restart uses the root saved in the version-1 XML. No automatic rollback or crash recovery is provided for staging or partially published migration folders.
