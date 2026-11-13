# Subnautica MP

Multiplayer for the original Subnautica (Steam, Mono, BepInEx 5), built from scratch. There is no separate server
program: a host is an ordinary peer that also relays traffic and checks who may write what. Everything is done at
runtime by the BepInEx plugin (Harmony patches and in-memory copies of existing buttons). No game file is edited, and
removing the plugin restores the stock game.

## Install

1. BepInEx is not in your game folder yet. Download **BepInEx 5.4.23.3 x64** from the BepInEx releases page, unzip it
   into the Subnautica folder, and start the game once so it creates `BepInEx\plugins`.
2. Copy the `dist\SubnauticaMP` folder from this project into `BepInEx\plugins`. These DLLs were compiled against your
   game's own assemblies. Or build it yourself:
   `dotnet build SubnauticaMP/SubnauticaMP.csproj -p:SubnauticaDir="C:\Program Files (x86)\Steam\steamapps\common\Subnautica"`
3. Every player needs the same plugin version and the same mods.

## How to use

**Main menu.** Open **Play**. The top right of that screen has two new controls, built at runtime from the game's own fonts and button art:
- **Host for friends: ON/OFF**: when ON, hosting starts by itself once the world you start or load has finished
  loading. Leave it OFF for normal modded Subnautica: nothing network-related runs.
- **Join**: opens a dialog with an address box (`ip`, `ip:port`, or `steam:<id>`), a Paste button, and one button per
  Steam friend who is hosting right now.

**In game.** The pause menu gets a **Multiplayer** button that opens a **Multiplayer** tab in Options (the tab exists only in a world):
- The toggle starts or stops hosting at any time. The tab shows how many players are connected.
- A coloured line says whether you still need to port forward (see below).
- **Copy join address** puts the address your friends must type on the clipboard. **Copy LAN address** does the same
  for players on your network.
- **Invite Steam friends** opens Steam's invite dialog. Friends can also click Join in their Steam friends list.
- Optional hosting banner at the top left: set `Interface/ShowHostingBanner = true`.

If the native tab fails to build (a game update changed the Options screen), the plugin logs the error and falls back
to plain on-screen panels with the same features.

**Pausing.** The pause menu freezes the world only when you play alone, or host with nobody connected. Once another
player is connected, pausing no longer stops time, so nobody else's game is frozen. If someone joins while you are
paused, time resumes at once. Only the pause menu's freeze is changed; PDA, loading screens and alt-tab are untouched.

## Connecting without port forwarding

| Method | Port forwarding? | How |
|---|---|---|
| **Steam join** | Never | Friends connect through Valve's relay network by Steam id. Works behind any router, including CGNAT. |
| **IP join with UPnP** | Automatic | When you host, the plugin asks your router to open the port (UPnP) and renews it every 30 minutes. |
| **IP join, manual** | Yes | If UPnP fails, the panel tells you the exact rule: "forward TCP port 7777 to 192.168.x.x". |

Real NAT punchthrough is not included. It needs a public rendezvous server to introduce two players, which would
bring back the separate server this project avoids. Steam's relay covers the same need without one.

**Telling you whether you must forward.** Your public address is read with STUN and compared with what your router
reports over UPnP. The panel then shows one of:
- *Green:* the port was opened automatically, no forwarding needed, and here is the address to share.
- *Amber:* UPnP did not work. You still need to forward the port; the rule is spelled out.
- *Red:* your router's address is not your public address (carrier-grade NAT), so forwarding cannot work. Use Steam
  join, a VPN, or ask your provider for a public address.

The check cannot prove a stranger can reach you from outside, because that needs a helper on the internet. "Green"
means the router agreed to open the port; a Windows firewall prompt can still block it.

## How other mods stay compatible

The join handshake compares the mod list (GUID and version) and a hash of every synced type's field layout, and the
host refuses a mismatch with a readable reason such as
`synced type Foo.Bar (mod FooMod) has different fields than the host's version`. Mods listed in `ClientOnlyMods`
(skins, UI) are skipped.

A mod's state can be synced three ways, from least to most work for the mod author:

1. **No change to the mod.** List the type and fields in `BepInEx\config\SubnauticaMP.synced.txt`:
   ```
   # Full.Type.Name:field1,field2
   MyMod.Reactor:power,online
   ```
2. **Attribute.** Reference `MPCore.dll` and mark fields: `[Synced] public int power;`,
   `[Synced(Quantize = 0.1f)] public float temperature;`. The plugin finds them automatically.
3. **Messages.** `session.RegisterMessage("mymod.event", (fromPeer, reader) => ...)` and `session.SendMessage(...)`.

## Telling our bugs from other mods' bugs

Every two seconds each owner publishes a hash of every synced field, and each receiver compares it with what it received
and with what is live in its game.

| Report | Meaning | Whose fault |
|---|---|---|
| `LocalOverwrite` | A mod or game code changed a synced field on a peer that does not own it. The value is restored. | Another mod, named in the report |
| `LostUpdate` | Receiver's copy differs from the owner's. It heals through a resync. | **This project** |
| `OwnershipViolation` | A peer wrote state for an object it does not own. The write is dropped. | This project, or a cheater |
| `Malformed` | A message could not be decoded. | This project, or mismatched builds |
| `UnboundComponent` | State arrived for a component the adapter never bound. | Adapter |

## What has been verified

| Part | Status |
|---|---|
| `MPCore` sync engine, UPnP/SOAP parsing, STUN parsing, port-forward advice, join-address parsing, TCP and merged transports | Unit tested: `dotnet run` in `MPCore.Tests` (86 checks pass) |
| Whole plugin, including Steam calls, Harmony patch targets, `Player.main`, the menu classes | Compiles against your game's DLLs and BepInEx |
| UPnP against a real router, Steam relay between two accounts, menu button placement and look | **Not run yet.** Needs a game session; I could not start the game. |

If the menu buttons look wrong, set `DumpMenuHierarchy = true` in the plugin config, start the game, and send the log:
it lists the menu object tree.

## Known limits and next milestones

- The two copies of Subnautica must be in a world for players to see each other. The world itself (inventory, building,
  creatures, time of day, saves) is not synced yet; each of those is a small adapter on the existing mechanisms.
- The PDA still freezes time in Subnautica's own way; only the pause menu freeze is gated by connected players.
- The native Multiplayer tab, the pause menu navigation and the pause gate compile against the real game DLLs but have not been run in game yet.
- Other players are a copy of the game's own player body with a name label, animated by the owner's real Animator
  parameters (swim, walk, turn, tools). It is copied from your own player, so it appears once you are in a world;
  if the copy fails the plugin logs it and shows a capsule. Equipment and suit variants are not synced yet.
- Joining from the main menu does not create a world for you (that would use a save slot). Start or load any world
  after connecting, or set `StartWorldAfterJoin = true`.
- TCP is used for everything, which is fine on LAN and acceptable online. The Epic and Xbox builds have no Steam join.
- The plugin menu panels use Unity's built-in GUI, so they look different from Subnautica's own widgets.
