### Fixed
- **Agent start**: the new resource sampler (and the fan profile loop) did their first, possibly slow pass before the web server started; on a machine with many disks the agent could miss the deploy health check. Both now start in the background.
- **Windows rollback**: when the stopped agent process still held its files, the rollback put the previous version *inside* the half-deleted folder and left the service broken. Deploy and rollback now wait for the process to exit (and kill it if needed), and the rollback refuses to move the previous version if the folder could not be removed. The deploy waits up to 60 s for the updated agent (was 30 s).

### New
- **Automation editor**: create, edit, delete and enable / disable tasks on the Automation page — steps with host / group checkboxes, timeouts, `force` / `always`, reordering, schedule presets and a "next run" preview. The page always shows the task grid (with **New task**), the help is behind **How it works**.
- **Group editor**: admins edit host groups in Power Control (renaming a group updates the tasks that use it; a group used by a task can't be deleted).
- **Comments in HomeLabControl.yaml are kept**: changes from the UI (deploy, Add existing, remove agent, tasks, groups) now rewrite only the changed blocks of the file instead of the whole file. If a patch can't reproduce the new config exactly, the file is written in full and the previous one is kept as `HomeLabControl.yaml.bak`.
