# Mod support

Subnautica MP is built so other mods keep working in multiplayer. Most mods need nothing at all. The rest can be
made multiplayer-aware in four ways, from no code at all to a full sync feature.

## What already works for every mod, without changes

- **Same world for everyone.** A joiner receives the host's whole save slot, including the folders other mods keep
  there (for example `SubnauticaRandomiser/`, `DeathrunRemade/`, Nautilus data). Both players start from the same
  modded state.
- **Modded creatures and vehicles.** Anything with the game's `Creature`, `Vehicle` or Cyclops `SubRoot` component is
  moved by the host (or its driver) and copied to the other players. If the other player does not have it yet, it is
  spawned from its prefab class id, which also works for prefabs registered by Nautilus/SMLHelper.
- **Modded items.** Item pickups, drops and locker contents are matched by the game's unique ids and spawned by
  `TechType`, which covers modded `TechType`s.
- **Mod list check.** When someone joins, both mod lists are compared. Differences are listed and the player can
  join anyway. Cosmetic or local-only mods can be excluded from the check in the config
  (`Compat/ClientOnlyMods`, comma separated plugin GUIDs).

## 1. No code: list fields in a text file

Create `BepInEx/config/SubnauticaMP.synced.txt` and add one line per component type:

```
# Full.Type.Name:field1,field2
SomeMod.Components.TurretController:isActive,targetAngle
SomeMod.Components.CustomLight:on,color
```

Every component of that type on an object of the shared world is then kept in step: when a player changes one of
those fields, the others receive the new value (last change wins). Both players need the same file. Supported field
types: numbers, bool, string, enums, `Vector2/3`, `Quaternion`, `Color`, byte arrays.

## 2. One attribute in your own mod

Reference `MPCore.dll` and mark fields with `[Synced]`:

```csharp
using MPCore.Sync;

public class MyPump : MonoBehaviour
{
    [Synced] public bool running;
    [Synced(Quantize = 0.01f)] public float pressure;
}
```

The plugin finds the type at startup and syncs it like the text-file types.

## 3. MPApi: send your own messages (soft dependency possible)

`SubnauticaMP.MPApi` uses only plain types, so you can call it directly (reference `SubnauticaMP.dll`) or through
reflection without a hard dependency.

```csharp
// in your plugin's Awake
MPApi.On("mymod.explode", (fromPeer, data) =>
{
    var pos = DecodeVector(data);
    SpawnExplosion(pos);              // runs inside ApplyRemote: your own patches will not send it back
});

// where the event happens in your mod
if (MPApi.IsMultiplayer && !MPApi.IsApplyingRemote) MPApi.Send("mymod.explode", EncodeVector(pos));
```

| Member | Use |
|---|---|
| `IsMultiplayer`, `IsHost`, `LocalPeer`, `IsApplyingRemote` | Session state. |
| `Send(name, bytes)`, `SendTo(peer, name, bytes)` | Send to everyone else, or to one player (0 = host). |
| `On(name, (peer, bytes) => ...)` | Receive. Stays registered across sessions. |
| `ApplyRemote(action)` | Apply a remote change without echoing it. |
| `RegisterSyncedType(type, fieldNames)` | Same as the text file, from code. Call in `Awake`. |
| `IdOf(gameObject)`, `Find(id)` | Match objects across machines by the game's unique id. |
| `SessionStarted`, `SessionEnded` | Events. |

Without a reference:

```csharp
var api = Type.GetType("SubnauticaMP.MPApi, SubnauticaMP");
bool mp = api != null && (bool)api.GetProperty("IsMultiplayer").GetValue(null, null);
if (mp) api.GetMethod("Send").Invoke(null, new object[] { "mymod.explode", bytes });
```

## 4. A full sync feature (ISyncFeature)

For anything more involved (spawning objects, host-decided outcomes), implement `SubnauticaMP.ISyncFeature` in your
own mod. Every class implementing it in any assembly that references `SubnauticaMP.dll` is started automatically for
each multiplayer session. The pattern, rules and helpers (`SyncKit`) are described in [SYNC_API.md](SYNC_API.md).
