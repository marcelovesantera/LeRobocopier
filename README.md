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

Copy, Move and Restore are unavailable. The backup root and preferences are displayed for reference.

A folder is registered as a single item: its contents are determined at the time of the operation. Returning from the editor keeps the project selected.

Configuration location: `%LOCALAPPDATA%\LeRobocopier\settings.xml` on Windows; `$XDG_CONFIG_HOME/LeRobocopier/settings.xml` on Linux, falling back to `~/.config` when XDG is missing, empty or relative. The default backup root is `LeRobocopier/Backups` inside the user's home folder. The XML file is created when the first project is saved. An unreadable file must be corrected before restarting the application. Existing version-1 XML names are preserved for compatibility.
