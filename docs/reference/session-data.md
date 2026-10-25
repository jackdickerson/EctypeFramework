# Session data and host-synced settings

`SessionData` carries values the host decides to every client, inside the session data the game
already sends. `SessionData.HostValue` builds a host-synced setting on top of it: a config entry
whose value is the host's on every client.

```csharp
// A value fixed for the whole session.
SessionData.Register<int>("my.plugin.guid/WaveSize",
    () => waveSize.Value,                    // host: produce once per session
    v  => Waves.Size = v,                    // every client, the host included
    new SessionDataOptions { MaxBytes = 256 });
int size = SessionData.Current<int>("my.plugin.guid/WaveSize");

// A setting only the host's config decides.
HostValue<float> spread = SessionData.HostValue(Config.Bind("General", "ArrivalSpread", 450f, "..."));
float metres = spread.Value;                 // read at the moment of use
spread.Changed += v => { /* ... */ };
```

Register at plugin `Awake`, on every client, from a key that is the same literal on every
client. Prefix keys with your plugin GUID.

## How a session reaches every client

The host starts every session, the hub included: on creating the room, on returning to the
hub, on launching a mission and on resuming a saved run. Each start serialises the session to
JSON (`GameSession.AsJObject`) once, before the ship and the session's networked objects are
spawned, and raises the JSON string as the game's event 9 to every other client. The event is
reliable and stored in the room's global event cache, so a player who joins later receives it
on joining, and it stays cached when the host leaves. Every other client rebuilds the session
from it (`GameSession.FromJObject`) and then loads it. The host never rebuilds its own
session from the JSON.

The framework adds one key to that JSON, `"ectype"`, holding every registered value:

```json
"ectype": { "v": 1, "keys": { "my.plugin.guid/WaveSize": 6, "...": "..." } }
```

Values are converted with the game's own Newtonsoft.Json (`JToken.FromObject` on the host,
`ToObject<T>` on the reader). A client without the framework ignores the key: the game reads
the session with Newtonsoft's object reader, which skips members it does not know, and reads
its own fields by name. A session for which no key produced a value carries no `"ectype"` key
at all.

### When each callback runs

- **`hostProduce`** runs on the host once per session, during the serialisation above. A null
  result leaves the key out of that session.
- **`apply`** runs on every client once per session that carries the key, and never for a
  session that does not. On the host it runs inside the serialisation, before the ship spawns.
  On every other client it runs when event 9 arrives, before the client loads the session's
  scene. A late joiner gets it from the cached event, as it joins. A player who joins during the
  end-of-run tally skips the cached session, as the game does, and gets the values with the next
  session. A mod that registers after a session arrived gets `apply` at once if the current
  session carries the key.
- **`SessionData.Changed`** is raised with a key whenever its current value appears, changes
  or disappears: at each session arrival, the host's own included, and when the client leaves
  the room.

Keep `apply` short: on the host it runs inside the game's session start.

### What "frozen" means

A value is captured when the host starts a session and cannot change until the host starts the
next one. There is no way to send it again: raising event 9 a second time makes every client
unload and reload the whole session. A value the host changes while in the hub reaches clients
through the session data only when the host next starts a session, which is the mission
launch. `HostValue` covers the hub with a second channel, below.

Losing the host does not start a new session. Every client keeps the values it has, and the
new host's values travel with the next session it starts.

### Reading

- `SessionData.Current<T>(key)`: the host's value for the current session, or `default(T)`.
- `SessionData.TryGetCurrent<T>(key, out value)`: false when the session lacks the key or it
  does not read as `T`.
- `SessionData.Has(key)` and `SessionData.SessionId`, the session's id.

A client whose session lacks a key keeps no value for it. Fall back to the local default.

## Size

Every joiner downloads the envelope with the session, so keep values small: numbers, flags,
short strings, small lists. A few hundred bytes per key is a good ceiling.

- Each key has a cap, `SessionDataOptions.MaxBytes`, default 4096 bytes of compact UTF-8 JSON.
- The whole envelope is capped at 16384 bytes.

The host leaves out a key over its own cap, and a key that would take the envelope over 16384
bytes, with one error naming it; keys are added in registration order. A client refuses an
envelope over 16384 bytes or in another format as a whole, and a key that does not read as its
registered type within its cap.

## Who may send it

The game's handler for event 9 takes the session from anyone: its only check is that the
receiving client is not the master. Photon stamps every event with its sender's actor number on
the server, so a client cannot forge it, and the framework records it around the game's
handler.

The game hands the master role to another player only when the master leaves the room. A
sender who is still in the room and is not the master therefore did not start this session:
the framework ignores its envelope, and the session arrives with no values. A cached event whose
sender has left the room is the session the previous host started, and is accepted.

This protects the framework's values only. The game itself still loads a session raised by
another player.

## Resumed runs

The game saves a run by keeping a fixed set of fields (the session id, the quest progress,
mutators, ship and resources) and rebuilds a new session object from them on resume. The id is
kept; the `"ectype"` key is not among those fields. The game saves endless runs, on the host,
each time the ship enters the void and when the crew saves and leaves.

So the host also keeps the values in a sidecar file:

- `BepInEx/config/jack.voidcrew.ectype.sessions/<session id>.json`, written when the host starts
  a mission that carries values, and again each time the game saves the run.
- When the host starts a session whose id has a sidecar, each key whose
  `SessionDataOptions.KeepOnResume` is true (the default) sends the value the run started with.
  Other keys, and keys the sidecar lacks, are produced fresh.
- A fresh session gets a fresh id, so it never matches a sidecar. Only files named by a session
  id are read or deleted.
- Sidecars last written more than 30 days ago are deleted, then all but the newest 64, each
  time one is written.

A run resumed on another machine has no sidecar there, and its values are produced fresh.

## Host-synced settings

`SessionData.HostValue(entry, key)` returns a `HostValue<T>`. `Value` is the value the host's
config decides; `Source` says which channel it came from; `IsFromHost` is false only while a
client is still on its own config; `Changed` is raised with the new value.

| Phase | Host reads | Every other client reads |
| --- | --- | --- |
| Hub | its own config | the host's room property, else the hub session's value, else its own config |
| Mission | the session's value | the session's value, else its own config |
| Outside a room | its own config | its own config |

"Mission" is decided by the current session data, so every client switches at the session
start, the host included. Mid-mission, the host's own `Value` is the frozen value too, so
host and clients agree.

The key defaults to `<config file name>/<section>.<key>`, the same on every client running the
same mod. The value also travels in the session data under that key, so it reaches late joiners
and resumed runs like any other session value.

### The hub channel

While the current session is the hub, the host keeps a room property named
`Ectype.<key>` equal to its own config, with the value
`{"a": <host's actor number>, "v": <value>}`. A change to the host's config reaches every
client within a second. Room properties arrive with the join, so a joiner has the value at
once.

Any player can write a room property. A client accepts the property only while:

- its stamp `a` is the current master's actor number, and
- the writer of its last change, when Photon reports one, is the master.

Photon reports the writer of each property change to the clients in the room as the sender of
its property-changed event. A client ignores a change written by anyone else and keeps the
value it had. The host sees the same event and writes its own value back.

Limits:

- The stamp is not a signature: every player knows the master's actor number. A joiner reads
  the room's properties from the join response, which names no writer, so the stamp alone
  decides there. A forged value stamped with the master's number and written after the host's
  last write is read by a joiner who arrives before the host has written its value back, one
  round trip later.
- When the host leaves, the room still holds the old host's stamp, which no client accepts.
  Clients use the hub session's value, then their own config, until the new host's own value
  arrives. The new host publishes its own config, not the old host's value. A player who writes
  a value stamped with the new host's number in that window is caught by the writer check on
  the clients present, and is read by anyone joining before the new host's write.

The host publishes only in the hub. A change to its config during a mission takes effect at
the next session it starts.

## Log lines

| Line | Meaning |
| --- | --- |
| `Ectype: session data: patched.` | The hooks are in. Logged once at start-up. |
| `Ectype: session data: sending <n> key(s) with <hub\|mission> session <id> (<bytes> bytes[, <k> restored from the saved run]).` | Host: the values that went out with this session. `<id>` is the first 8 characters of the session id. |
| `Ectype: session data: received <n> key(s) from the host (actor <a>) with <hub\|mission> session <id>.` | Client: a session carrying values arrived. A late joiner logs it on joining. |
| `Ectype: session data: key '<key>' is <n> bytes, over its <cap>-byte cap; left out of this session.` | Host: the value is too big. Shrink it or raise `MaxBytes`. |
| `Ectype: session data: key '<key>' would bring the envelope to <n> bytes, over the 16384-byte cap; left out of this session.` | Host: the keys registered before this one used the space. |
| `Ectype: session data: producing key '<key>' threw; left out of this session: <exception>` | Host: the producer threw. |
| `Ectype: session data: applying key '<key>' threw: <exception>` | Any client: the `apply` callback threw. Other keys still applied. |
| `Ectype: session data: key '<key>' from the host does not read as <Type> within <cap> bytes; this client keeps no value for it.` | The host sent a value of another shape, usually another version of the mod. Logged once per key. |
| `Ectype: session data: ignored the host's envelope: <problem>.` | The envelope is over 16384 bytes, not an object, or in a format this framework does not read. |
| `Ectype: session data: ignored the session data from actor <a>, who is in the room and is not the master client (master is actor <m>).` | Another player raised the game's session event. The game still loads that session; the framework keeps no values from it. |
| `Ectype: session data: saved-run sidecar for session <id> unreadable; values are produced fresh: <message>` | The sidecar exists and could not be read. |
| `Ectype: session data: saved-run sidecar write failed (<path>): <message>` | The config folder is not writable. A resumed run then gets fresh values. |
| `Ectype: session data: '<key>' not registered: the session-data patches are not applied this session, so no host value reaches this client.` | The framework is inert or the hooks were not found. `HostValue` then follows the local config. |
| `Ectype: session data: GameSession.AsJObject / FromJObject not found (game update?); session data and host values follow each client's own config.` | A game update renamed the hooks. |
| `Ectype: host value '<key>': ignored a room-property write from actor <a>, who is not the master client (<m>).` | Client: another player wrote the host's room property. Logged once per writer. |
| `Ectype: host value '<key>': actor <a> wrote the room property; republishing the host's value.` | Host: the same event, seen from the host. |
| `Ectype: host value '<key>' published to the room[ (the room held another value)].` | Verbose. The host wrote its value. |
| `Ectype: host value '<key>': room property not used (<reason>).` | Verbose. The property was absent, stamped by another actor, over the cap, or not JSON. |

`VerboseLogging` also logs each sidecar write and prune.

## Checking it in game

With two clients and `VerboseLogging` on both:

1. Host in the hub, change the setting in the host's config: the client logs the new value
   from `Room`.
2. Launch a mission: both log `Session` with the host's value; the host logs `sending`, the
   client `received`.
3. Join a third client mid-mission: it logs `received` with the same id and reads the host's
   value.
4. Save an endless run, return to the hub, resume it: the host logs
   `restored from the saved run`, and clients read the value the run started with even if the
   host's config changed since.

Sample: [examples/session-data](../../examples/session-data/).
