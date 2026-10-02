### Changed
- **Groups in Power Control** are a grid below the hosts (hosts, online count, WOL all / Shutdown all, and Edit / Delete for admins); a group is edited in its own small form instead of one long dialog with all groups.

### Fixed
- **Deploy / Update all window**: the log is live (including upload progress of the package, which takes minutes on a slow link), and the window can be hidden while the update keeps running — **Show progress** / **Show last log** brings it back. Before, both close buttons were disabled until the whole update finished, which looked like a hang.
