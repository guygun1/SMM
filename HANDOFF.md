---
saved-at: 2026-11-13T10:09:57Z
saved-at-sha: unknown
saved-at-branch: unknown
saved-from-cwd: C:\Users\guyde\Documents\IdeaProjects\C#\Mods\unity\Subnautica\multiplayer
---

Pick up the Subnautica MP mod (BepInEx plugin `SubnauticaMP` + `MPCore` library). No git repo here, no dotnet: edit in the work copy, commit files to the user's disk with device_commit_files (stage to a NEW outputs/vN dir first, commit in a separate step, then stage back and compare sizes: commits have raced and left stale files before). The user builds with `build-and-deploy.bat` (game closed) and pastes errors/logs. Never build or control the PC without asking. User likes the `/caveman` voice (plain sentences for questions and irreversible actions).

Next step: wait for the user's next log with advanced debug ON (Multiplayer tab toggle or Ctrl+F9), taken as the joiner, while docking the Seamoth at the Cyclops bay and entering through the hatch tube. Read `sound:`, `hatch:`, `prox:`, `dock:` lines to find what still restarts the hatch / docking-bay sounds and the stuck entry animation. Do not guess more fixes before that evidence.

Recent unverified changes (all committed, none built or tested at save time): PlayerGuard carries the player with CharacterController.Move instead of toggling the controller (that toggle fired every trigger exit/enter each frame); hatch/bay sound debounce (DebugSound.cs); docking-bay animators excluded from anim forwarding both ways; AttachDock only moves a parked vehicle if off by more than 2 cm / 1 degree; proximity probe and hatch probe in the debug log; top-right debug HUD (Gui.cs); host heartbeat every 2 s plus a joiner watchdog (20 s silence returns to the main menu; SyncSession.SilentMs / DropHost). First check that the latest build compiled; if the user reports errors, compare disk vs work-copy file sizes before assuming a code bug.

Still open: Cyclops helm menus flashing, battery readout nonsense (power relay reads 0 / 0), after Unstuck the player dropped about 30 m, "Kinematic body only supports Speculative Continuous" warning spam from EntityFollower, ProgressSync NRE at world load (PDAEncyclopedia.ContainsEntry), per-player progression persistence, fire/leak event ids unverified, worn equipment visuals, tool effects.
