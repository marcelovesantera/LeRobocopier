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
- Keep full source paths under each project's backup folder, separating drive letters, UNC shares and the Linux root.
- Check paths and links before copying, then verify copied files by size and streaming SHA-256.
- Show preparation, copying, verification and metadata progress with warnings, failures and partial effects in a modal dialog.
- Persist the independent Copy confirmation preference and re-enable it in Settings.
- Prevent concurrent operations, registry changes during execution and another instance using the same configuration.

Move and Restore remain unavailable. The backup root is displayed for reference. During Copy, the operation dialog and main window cannot be closed normally. There is no cancellation while running.

A folder is registered as a single item: its contents are determined at the time of the operation. Returning from the editor keeps the project selected.

Configuration location: `%LOCALAPPDATA%\LeRobocopier\settings.xml` on Windows; `$XDG_CONFIG_HOME/LeRobocopier/settings.xml` on Linux, falling back to `~/.config` when XDG is missing, empty or relative. The default backup root is `LeRobocopier/Backups` inside the user's home folder. The XML file is created when a project or confirmation preference is first saved. An unreadable file must be corrected before restarting the application. Existing version-1 XML names are preserved for compatibility.

Copy preserves modification times, common file attributes and Linux permissions, including executable bits. It does not replicate ownership or ACLs. External programs can still change files; reported failures do not roll back completed replacements. Linux file-type validation uses `statx` to reject special files as well as symbolic links.
