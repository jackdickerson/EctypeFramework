# Host room values

`SessionData.HostRoomValue` makes a value only the host writes and every client reads, for as
long as the room lasts, in the hub and in a mission alike. It suits choices the host makes while
playing: a list of ship types picked in the hub, whether teams are confirmed, which panels are
sabotaged until when.

```csharp
HostRoomValue<List<int>> types = SessionData.HostRoomValue("com.example.mymod/shipTypes", new List<int>());

List<int> now = types.Value;              // the host's value on every client; the default before any arrives
bool written = types.Set(new List<int> { 2, 0, 1 });   // host only; false on a client
types.Changed += v => Redraw(v);          // raised when the value this client holds changes
```

Register at plugin `Awake`, on every client, with a key that is the same literal on every client:
`"<plugin GUID>/<name>"`, the [identifier rule](../identifiers.md). A refused registration logs one
error and returns a value that keeps its default and cannot be set; the method never returns null.

Choosing between the three host-decided channels:

| The value is | Use |
| --- | --- |
| a setting in the host's config file | `SessionData.HostValue` ([session-data.md](session-data.md#host-synced-settings)) |
| decided once per session, and must come back with a resumed run | `SessionData.Register` ([session-data.md](session-data.md)) |
| a choice the host makes at any time, kept for the life of the room | `SessionData.HostRoomValue` |

## How a value travels

Each value is one room property, `Ectype.Value:<key>`, holding the value's compact JSON text.
Values are converted with the game's own Newtonsoft.Json, as session data is.

- **On the host.** `Set` converts the value to JSON, reads it back into a fresh object and holds
  that as `Value` at once; `Changed` is raised before `Set` returns. A value whose JSON text equals
  the one held writes nothing. The framework's per-frame step then writes every value set since
  the last step in one `SetCustomProperties` call, so the values set in one frame reach every
  client in one property-changed event.
- **On a client.** Photon merges the change into the room's copy and raises its property-changed
  event (253). The framework reads every host room value in that event, then raises `Changed` for
  each one that changed, so a handler sees every value of the batch.
- **A joiner** receives the room's properties in the join response and reads every value from it.
  That is the only catch-up: there is no other late-join path, and none is needed, because the
  room holds the current value of every key.
- **Across sessions.** Room properties belong to the room, not to a session. Starting a mission or
  returning to the hub neither sends nor clears them; a value set in the hub is still the value in
  the mission. A mod that wants a fresh value for a mission sets it.
- **Leaving the room** returns every value to its default, with `Changed`.

`HasValue` is false while `Value` is the default. `CanSet` is true while this client is the master
client of a room.

### One property per value

`HostValue` packs every key into one room property, because config settings are scalars that
change a few times a session. Host room values each get their own property:

- **One key's change rewrites only that key.** Photon sends only the properties a call names. In a
  shared property, flipping one bool resends every value, up to 16 KB, to every player, and every
  client parses all of them again.
- **They change often.** A hub choice changes on each click; an in-mission list changes on each
  event the host decides. Resending everything at that rate is the cost a shared property would
  multiply.
- **They are bigger.** Lists and small classes run to hundreds of bytes or a few kilobytes each,
  so the shared property would sit near its cap.
- **Batching does not need a shared property.** One `SetCustomProperties` call naming several
  properties is one operation on the server and one event on every client, which carries all of
  them together.
- **A forged write touches one value.** The host puts back only the properties another player
  wrote.

Each update carries its property names, a few dozen bytes each.

## Who may write

Any player can write any room property. Photon's server stamps every property-changed event with
the actor number of the player whose call made the change, the event's sender, and delivers a
room's events to every client in the order the server processed them. So:

- A client takes a change only when its sender is the master client as the change arrives. A
  change written by anyone else is ignored, and the client keeps the value it had.
- The host ignores the echo of its own writes. When another player's write leaves the room holding
  something other than the host's value, the host writes its own value back at the next frame.
- A change Photon reports no writer for (sender 0, which only a server-side plugin produces) is
  taken.

### Why the writer replaces timing

A host that judges the room's copy by its content cannot tell its own write that has not come back
yet from somebody else's write. The room's copy of its own write changes a round trip later, and
later still while Photon holds incoming messages during a scene load or a long frame. A content
check therefore has to wait: for the echo of the host's last write, for a difference that lasts,
for messages to flow again. During that wait every client shows the forged value, and an echo
slower than the window is mistaken for a forgery.

The writer of each change is recorded by the server on the event itself. A client knows who wrote
a change the moment it reads it, however late that is, so it never shows a forged value. The host
never reads its own values from the room: it goes by its own copy, and compares the room's copy
only when an event names another writer. Held messages delay when the check runs, never what it
decides, so no clock or window is involved.

## Host migration

The game hands the master role to another player only when the master leaves the room. The new
master keeps the values it held as a client as its own: the room's values, except a write it
refused because its writer was not the master. It then compares each with the room's copy and
writes back, in one update, every value the room holds differently: a forged write the departed
host had not yet put back. From then on it guards them as above. A player who loses the master
role before its pending writes went out takes the room's values for those keys instead.

## Types and size

`T` is anything `SessionData.Register` accepts, with the same checks: plain JSON data, refused as a
whole when a Unity type, a delegate, a pointer or an abstract type appears anywhere in it, or when
a default value does not survive a JSON round trip ([session-data.md](session-data.md#types-and-size)).
Registration also refuses a default value over the cap.

- `maxBytes` caps the value's compact UTF-8 JSON, default 4096, clamped to 16384. `Set` refuses a
  value over it with `false` and one error.
- Every host room value together, across all mods, is capped at 16384 bytes, counted from the
  values the host holds. `Set` refuses a value that would go over it.
- A client refuses a value over its cap, not JSON text, or not reading as `T`, and keeps the value
  it had; one warning per key per room.

Every joiner downloads every room property on joining, so keep values small.

`Value` is the same object for every reader on a client. `Set` copies its argument through JSON,
so changing the argument afterwards changes nothing.

## Limits

- A joiner reads the join response, which names no writer. A value another player wrote after the
  host's last write is what a joiner reads if it arrives before the host has put its own back; the
  put-back reaches it as a change one round trip later.
- A client whose copy of the mod reads a value differently (another version) keeps its previous
  value, and the default until one reads.
- Only the host writes. A client that wants a value changed asks the host through the mod's own
  message, and the host decides and calls `Set`.

## Things that look like they should work

- **Changing `Value` in place.** It changes this client's object only; nothing is written, and the
  host's next update replaces it. Build a new value and `Set` it.
- **Writing `Ectype.Value:<key>` with `SetCustomProperties`.** From a client, every other client
  ignores it and the host puts its own back. From the host, the host's own copy does not change, so
  its `Value` disagrees with every client's until it next sets the key.
- **Reading the room property on the host.** It holds the host's last write a round trip late.
  Read `Value`.
- **Calling `Set` before the room exists or on a client.** It returns `false` and holds nothing.
  Seed a value from the first frame `CanSet` is true.
- **Expecting a value to reset at mission start.** It belongs to the room. Set what the mission
  starts from.
- **Waiting for `Changed` on the host after `Set`.** It is raised inside `Set`, before it returns.

## Log lines

| Line | Meaning |
| --- | --- |
| `Ectype: host room value '<key>' refused: <problem>; it keeps its default on this client.` | The key is empty, too long, holds a control character, does not follow the [identifier rule](../identifiers.md) or is already registered; `T` does not survive JSON; or the default is over the cap. |
| `Ectype: host room value '<key>' not registered: the framework is inert this session; it keeps its default.` | Patching failed at load, or the framework is disabled. |
| `Ectype: host room value '<key>' not written: <problem>.` | Host: the value is over its cap, could not be serialised, or does not read back as `T`. `Set` returned false. Logged once per key and problem. |
| `Ectype: host room value '<key>' not written: it would bring the host room values to <n> bytes, over the 16384-byte cap.` | Host: the values together are too big. `Set` returned false. |
| `Ectype: host room value '<key>': the room's value does not read as <Type> within <cap> bytes (<problem>); this client keeps the value it had.` | The host runs another version of the mod, or a value in the room is not JSON text. Logged once per key per room. |
| `Ectype: host room value '<key>' Changed subscriber <Type>.<method> threw: <exception>` | A `Changed` subscriber threw. The others still ran. |
| `Ectype: host room values: ignored a write of <keys> from actor <a>, who is not the master client (<m>).` | Client: another player wrote a host room value. Logged once per writer per room. |
| `Ectype: host room values: actor <a> wrote <keys>; the host puts its own back.` | Host: the same write, seen from the host. |
| `Ectype: host room values: this player hosts the room now and keeps the <n> value(s) it held; <k> differed in the room and are put back (<keys>).` | New host: the room held values the previous host had not put back. |
| `Ectype: host room values: this player hosts the room now and keeps the <n> value(s) it held.` | Verbose. New host, or the player who created the room. |
| `Ectype: host room values: wrote <n> value(s) in one room update (<keys>).` | Verbose. The host's batch for this frame. |
| `Ectype: host room values: the room callbacks could not be added; joins and host changes are noticed at the next frame: <message>` | Photon refused the callback target. Values still follow the room a frame later. |
| `Ectype: host room values: update failed (reported once): <exception>` and `Ectype: host room values: reading a room update failed (reported once): <exception>` | The channel threw. Values keep what they held. |

## Checking it in game

With two clients and `VerboseLogging` on both:

1. Host in the hub, call `Set` twice in one frame on two values: the host logs one `wrote 2
   value(s) in one room update`; the client's `Changed` handlers each run once, after both values
   are in.
2. Join a third client: it reads both values on joining and raises `Changed` once for each.
3. Launch a mission: both values are unchanged on every client.
4. From the second client, write `Ectype.Value:<key>` with `SetCustomProperties`: the third client
   logs `ignored a write`, the host logs `the host puts its own back`, and `Value` stays the
   host's on every client.
5. Let the host leave: the new host logs `hosts the room now`, and `Set` works there.
