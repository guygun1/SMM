# Writing a sync feature (SyncKit)

A sync feature makes one kind of game change visible to every player: a door opening, an item going into a locker,
a base piece being built. Each feature is one file in `SubnauticaMP/Features/`. It is found and started
automatically when a multiplayer session starts. No other file needs to change.

## How the shared world works (what you can rely on)

- When a player joins, the host sends its save and the joiner loads it. From then on, every object that came from the
  save has the **same unique id** (`UniqueIdentifier.Id`, a GUID string) on every machine. That id is how features
  find "the same object" on the other side: `SyncKit.IdOf(component)` on the sender, `SyncKit.Find(id)` on the receiver.
- Objects created after the join (a dropped item, a new base piece) get a new id on the machine that created them.
  To keep them matchable, the receiver must create its copy and set the same id: `uid.Id = id` on its
  `UniqueIdentifier` (see `WorldSync.SpawnDrop` for a working example).
- The host is peer 0. Joiners are 1, 2, ... Every message goes through the host, which forwards it.
- Already synced elsewhere, do not duplicate: player position, body and animation (`Plugin`, `PlayerAvatar`);
  time of day, world item pickups and drops (`WorldSync`); creature, vehicle and Cyclops movement, vehicle seats
  (`EntitySync`); player versus player damage (`PvP`).

## The pattern

```csharp
using HarmonyLib;
using MPCore.IO;

namespace SubnauticaMP.Features
{
    sealed class DoorSync : ISyncFeature
    {
        public string Name { get { return "doors"; } }

        public void Register()
        {
            // 2. the other side: find the same object and replay the change inside SyncKit.Apply
            SyncKit.On("door.set", (from, r) =>
            {
                string id = r.Str(), path = r.Str(); bool open = r.Bool();
                var t = SyncKit.FindChild(id, path);
                var door = t != null ? t.GetComponent<Openable>() : null;
                if (door == null) return;                       // not loaded here: fine, skip
                SyncKit.Apply(() => door.PlayOpenAnimation(open, door.animTime));
            });
        }

        public void Tick() { }
    }

    // 1. this side: a Harmony postfix on the game method that made the change
    [HarmonyPatch(typeof(Openable), "PlayOpenAnimation")]
    static class P_DoorOpen
    {
        static void Postfix(Openable __instance, bool openState)
        {
            if (!SyncKit.ShouldSend) return;                    // no session, or this IS a remote change being applied
            string id, path;
            if (!SyncKit.IdAndPath(__instance, out id, out path)) return;
            SyncKit.Send("door.set", w => { w.Str(id); w.Str(path); w.Bool(openState); });
        }
    }
}
```

Rules that keep features from fighting each other:

1. **Send only when `SyncKit.ShouldSend`.** It is false with nobody connected and while a remote change is being
   applied, so a replayed change is never sent back (no echo loops).
2. **Replay remote changes inside `SyncKit.Apply(() => ...)`.**
3. **Message names start with your feature's prefix** (`door.`, `locker.`, `build.`) so they never collide.
4. **Missing objects are normal.** The other player may be far away, so the object is not loaded. Return quietly.
5. **Never throw out of a patch.** Wrap risky code in try/catch. A throwing postfix can break the game method.
6. **Prefer postfixes on the game's own state-changing method** over polling in `Tick`. Use `Tick` only when there
   is no single method to patch (then compare with the last sent value and send on change, at most a few times a second).
7. **Host authority when two players could disagree** (for example, two players taking the last item from a locker):
   send the request to the host with `SyncKit.SendTo(0, ...)`, let the host apply it and broadcast the result.
8. **Log sparingly** with `SyncKit.Info("feature: ...")`: one line per real event, never per frame.

## API summary

| Call | What it does |
|---|---|
| `SyncKit.ShouldSend` | A session with another player is running and this is not a replay. Check before every send. |
| `SyncKit.Active`, `IsHost`, `LocalPeer`, `Applying` | Session state. |
| `SyncKit.Send(name, w => ...)` | Send to everyone else. |
| `SyncKit.SendTo(peer, name, w => ...)` | Send to the host (`0`) or one peer. |
| `SyncKit.On(name, (from, r) => ...)` | Handle a message (call in `Register`). Exceptions are caught and logged. |
| `SyncKit.Apply(() => ...)` | Replay a remote change without echoing it. |
| `SyncKit.IdOf(c)`, `SyncKit.Find(id)`, `SyncKit.Find<T>(id)` | Match objects across machines by unique id. |
| `SyncKit.IdAndPath(c, out id, out path)`, `SyncKit.FindChild(id, path)` | For child objects without their own id (a door inside a base piece). |
| `SyncKit.Vec/Rot/Tech` | Write and read `Vector3`, `Quaternion`, `TechType`. |
| `ByteWriter` / `ByteReader` | `U8 Bool VarU VarI F32 F64 Str Bytes`; read in the same order you wrote. |

## Building and checking

- Find exact game method signatures (never guess them):
  `dotnet <scratchpad>/sig/sig.dll <Managed>/Assembly-CSharp.dll '^TypeName$' 'MemberRegex'`
  (also try `Assembly-CSharp-firstpass.dll`). It prints visibility, return type and parameter names.
- Compile your feature alone against the real game DLLs:
  `bash <scratchpad>/tools/build_feature.sh SubnauticaMP/Features/YourFeature.cs` must print `BUILD OK`.
- Private game members: use `AccessTools.Method/Field` or `Traverse`, and in Harmony patches use the
  parameter names that `sig.dll` prints (or `__0`, `__1`).
