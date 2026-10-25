# Session data and host-synced settings

`SessionData` carries values the host decides to every client, inside the session data the game
already sends. `SessionData.HostValue` builds a host-synced setting on top of it: a config entry
whose value is the host's on every client.

```csharp
// A value fixed for the whole session.
SessionData.Register<int>("com.example.mymod/WaveSize",
    () => waveSize.Value,                    // host: produce once per session
    v  => Waves.Size = v,                    // every client, the host included
    new SessionDataOptions { MaxBytes = 256 });
int size = SessionData.Current<int>("com.example.mymod/WaveSize");

// A setting only the host's config decides.
HostValue<float> spread = SessionData.HostValue(Config.Bind("General", "ArrivalSpread", 450f, "..."));
float metres = spread.Value;                 // read at the moment of use
spread.Changed += v => { /* ... */ };
```

Register at plugin `Awake`, on every client, from a key that is the same literal on every
client. A key is `"<plugin GUID>/<name>"`, the [identifier rule](../identifiers.md); `Register`
and `HostValue` refuse any other.

## How a session reaches every client

The host starts every session, the hub included: on creating the room, on returning to the
hub after a run, on launching a mission and on resuming a saved run. Each start serialises the
session to JSON once (`GameSession.AsJObject`) and raises the JSON string as the game's event 9
to every other client. The event is reliable and stored in the room's global event cache, so a
player who joins later receives it on joining; the host clears that cache at the start of each
session. Every other client rebuilds the session from it (`GameSession.FromJObject`), then runs
its own start sequence. The host never rebuilds its own session from the JSON.

The framework adds one key to that JSON, `"ectype"`:

```json
"ectype": { "v": 1, "a": 1, "keys": { "com.example.mymod/WaveSize": 6 } }
```

`a` is the actor number of the host that produced the values. Values are converted with the
game's own Newtonsoft.Json (`JToken.FromObject` on the host, `ToObject<T>` on the reader). A
client without the framework ignores the key: the game reads the session with Newtonsoft's
object reader, which skips members it does not know, and reads its own fields by name. A
session for which no key produced a value carries no `"ectype"` key at all.

### When each callback runs

Host and clients run the same start sequence: fade out, unload every scene but the bootstrap
scene (the hub, the main menu or the end-of-run screens), load the session scene, create the
game-state object, then host or join the session. Joining or hosting first unloads the previous
session's assets and then makes the new session object the active one
(`GameSessionManager.PrepareNextSession`). The framework acts there, on every machine:

- **`hostProduce`** runs on the host once per session, as the new session object becomes
  active. `GameSessionManager.ActiveSession` is then the new session: its id, whether it is the
  hub, and for a mission the quest asset, seed, ship loadout and starting mutators are set. The
  quest has not been generated, no sector has been entered, and no ship or networked object
  exists. The previous scene is gone: hub objects such as the quest, ship and mutator managers
  no longer exist. A value that depends on hub state is read while the hub is loaded and cached
  by the mod, and the producer returns the cached value. A null result leaves the key out.
- **`apply`** runs on every client at the same point of its own start sequence, the host
  included: after the previous scene is unloaded, before the quest is generated. Host and
  clients therefore hold the same values while each generates the quest and enters the first
  sector. It runs once per session that carries the key, never for a session that does not. A
  late joiner runs the same sequence on joining. `apply` must not touch scene objects: none of
  the new session's exist yet. The game checks a joining client's registered content after this
  point, so `apply` can run for a session the client is then refused from; the values are cleared
  when it leaves the room. A mod that registers after a session became active gets `apply` at
  once if the session carries the key.
- **`SessionData.Changed`** is raised with a key whenever its current value appears, changes
  or disappears: at each session start and when the client leaves the room.

Between the moment event 9 arrives and its own session start, a client keeps the previous
session's values: the previous session is still the one running there.

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
- `SessionData.Has(key)`.
- `SessionData.SessionId`: the id the host gave the session. On a client it differs from
  `GameSessionManager.ActiveSession.GameSessionID`: the game gives a joining client's session
  object a new random id as it activates it.

A client whose session lacks a key keeps no value for it. Fall back to the local default.

## Types and size

`T` must be plain JSON data: bools, numbers, enums, strings, and arrays, lists, dictionaries or
classes of those. `Register` refuses, with one error, a Unity object, any type from Unity's
namespaces (vectors, colours: the game's serializer has no converter for them), a delegate, a
pointer, an interface or abstract type, wherever it appears: as `T` itself, as an array element
or generic argument, or as the type of a serialised public field or property of a class, three
levels deep (members marked `[JsonIgnore]` are skipped). It also refuses a type whose default
value does not survive a JSON round trip. A `HostValue` takes bools, numbers, enums and strings
only.

Every reader of these values parses JSON with Newtonsoft's date detection off, so a string that
looks like a date arrives as the same string. A `decimal` travels as a string, so it keeps every
digit; `float` and `double` round-trip exactly as numbers. Enums travel as their numbers.

Every joiner downloads the envelope with the session, so keep values small. A few hundred bytes
per key is a good ceiling.

- Each key has a cap, `SessionDataOptions.MaxBytes`, default 4096 bytes of compact UTF-8 JSON.
  A key over it is left out with one error naming it.
- The whole envelope is capped at 16384 bytes. While it is over, the largest key is left out,
  with one error naming it and its size, until the rest fits.

A client refuses an envelope over 16384 bytes or in another format as a whole, and a key that
does not read as its registered type within its cap.

## Who may send it

The game's handler for event 9 takes the session from anyone: its only check is that the
receiving client is not the master. The game hands the master role to another player only when
the master leaves the room. The framework checks what it can:

- **Live delivery.** Photon stamps a live event with its sender's actor number on the server,
  and the framework records it around the game's handler. The envelope must come from the actor
  its `a` names, and that actor must be the master or have left the room.
- **From the room cache.** Photon delivers the room's global cache as actor 0, so a late joiner
  cannot tell who raised the cached session. The envelope is accepted when its `a` names the
  current master or any actor number not in the room now, one that never joined included. The
  only check a joiner can make is that `a` does not name a present player other than the master.

Limits:

- `a` is not a signature. A player who raises event 9 with caching and names the master, or any
  actor number not in the room (their own after leaving, or one that never existed), is
  indistinguishable from the host to every player who joins afterwards, until the host starts
  its next session and clears the cache. The players present when such an event is raised refuse
  it, because its live sender is not the actor it names or is not the master.
- The game itself trusts the same event: every client present loads a session another player
  raises, and a joiner loads the cached one. The framework only decides whether to keep the
  envelope's values.

## Resumed runs

The game saves a run by keeping a fixed set of fields (the session id, the quest progress,
mutators, ship and resources) and rebuilds a new session object from them on resume, with the
saved id. The `"ectype"` key is not among those fields. The game saves endless runs on the host,
each time the ship enters the void and when the crew saves and leaves.

So the values are also kept in a sidecar file:

- `BepInEx/config/jack.voidcrew.ectype.sessions/<id>.json`, written each time the game saves a
  run that carries values, by whichever machine saves it, named by the id the save carries.
- The id the save carries is the saving machine's own session id. A player who became host
  mid-run has a session id the game gave them on joining, not the original host's, and their
  saves carry that one; the sidecar follows it, so a resume from their save finds the values.
- When the host starts a session whose id has a sidecar, each key whose
  `SessionDataOptions.KeepOnResume` is true (the default) sends the value the run started with.
  Other keys, and keys the sidecar lacks, are produced fresh.
- A fresh session gets a fresh id, so it never matches a sidecar. Only files named by a session
  id are read or deleted.
- The file is written beside the old one and swapped in, so a crash leaves either the old file
  or the new one. Leftover temporary files are deleted, then sidecars last written more than 180
  days ago, then all but the newest 16, each time one is written. The game keeps one saved run per
  profile, so the newest file is the one a resume needs.

The game keeps the saved run in the player's profile, so a run can be resumed in a later game
process; the sidecar is a file beside the framework's config and lasts as long. A run resumed on
another machine, or on one whose config folder was cleared, has no sidecar there, and its values
are produced fresh.

## Host-synced settings

`SessionData.HostValue(entry, key)` returns a `HostValue<T>`. `Value` is the value the host's
config decides; `Source` says which channel it came from; `IsFromHost` is false only while a
client is still on its own config; `Changed` is raised with the new value.

| Phase | Host reads | Every other client reads |
| --- | --- | --- |
| Hub | its own config | the host's room property, else the hub session's value, else its own config |
| Mission | the session's value | the session's value, else its own config |
| Outside a room | its own config | its own config |

"Mission" is decided by the current session data, so every machine switches at its own session
start, the host included. Mid-mission, the host's own `Value` is the frozen value too, so host
and clients agree.

The key defaults to `<config file name>/<section>.<key>`, the same on every client running the
same mod. The plugin's own `Config` file is named after its GUID, so the default follows the
identifier rule; an entry bound in a config file of another name needs an explicit key. The value also travels in the session data under that key, so it reaches late joiners
and resumed runs like any other session value.

### The hub channel

While the current session is the hub, the host keeps one room property,
`Ectype.HostValues` (`SessionData.HostValuesRoomProperty`), equal to its own config for every
host value:

```json
{ "a": <host's actor number>, "v": { "<key>": <value>, ... } }
```

The host rebuilds the property only when one of its values changes or the master changes, and
writes it when the room's copy differs. A change to the host's config reaches every client
within a second. Room properties arrive with the join, so a joiner has the values at once. The
property is capped at 16384 bytes, with 4096 per value; over the total, the largest value is left
out with one error naming it.

Any player can write a room property. A client accepts the property only while:

- its stamp `a` is the current master's actor number,
- the writer of its last change, when Photon reports one, is the master, and
- each value is of its setting's kind (a number for a number, a bool for a bool, a string or
  null for a string, a number or name for an enum).

Photon reports the writer of each property change to the clients in the room as the sender of
its property-changed event, whatever the value written, a null or a deletion included. A client
ignores a change written by a player who is not the master when the change arrives, and keeps
the values it had. The host sees the same event and writes its own values back.

Limits:

- The stamp is not a signature: every player knows the master's actor number. A joiner reads
  the room's properties from the join response, which names no writer, so the stamp alone
  decides there. A forged value stamped with the master's number and written after the host's
  last write is read by a joiner who arrives before the host has written its values back, one
  round trip later.
- When the host leaves, the room still holds the old host's stamp, which no client accepts once
  the master has changed: its last write was the master's when it arrived, so it is not treated
  as a forgery, and the stamp check refuses it. Clients use the hub session's value, then their
  own config, until the new host's own values arrive. The new host publishes its own config, not the old host's values. A player who writes
  values stamped with the new host's number in that window is caught by the writer check on the
  clients present, and is read by anyone joining before the new host's write.

The host publishes only in the hub. A change to its config during a mission takes effect at
the next session it starts.

## Log lines

`<id>` is the first 8 characters of a session id.

| Line | Meaning |
| --- | --- |
| `Ectype: session data: patched.` | The hooks are in. Logged once at start-up. |
| `Ectype: session data: GameSession.AsJObject / FromJObject or GameSessionManager.PrepareNextSession not found (game update?); session data and host values follow each client's own config.` | A game update renamed a hook. |
| `Ectype: session data: PhotonService.OnEvent(EventData) not found (game update?); the session data's sender is not checked.` | The envelope's stamp is still checked against the master on receipt. |
| `Ectype: session data: GameSession.GetPreservedState not found (game update?); a saved run's sidecar is written at mission start instead, under the id the session started with.` | The sidecar holds the values the mission started with. A run saved by a host who took over mid-run loses its values on resume, because its save carries another id. |
| `Ectype: session data: Register('<key>') refused: <problem>.` | The key is empty, longer than 128 characters, holds a control character, does not follow the [identifier rule](../identifiers.md) or is already registered; no producer was given; or `T` does not survive JSON. |
| `Ectype: session data: '<key>' not registered: the session-data patches are not applied this session, so no host value reaches this client.` | The framework is inert or a hook is missing. `HostValue` then follows the local config. |
| `Ectype: session data: sending <n> key(s) with <hub\|mission> session <id> (<bytes> bytes[, <k> restored from the saved run]).` | Host: the values that go out with this session. |
| `Ectype: session data: received <n> key(s) from the host (actor <a>[, from the room cache]) with <hub\|mission> session <id>.` | Client: a session carrying values arrived. A late joiner's comes from the room cache. |
| `Ectype: session data: applied <n> key(s) locally.` | Verbose. The values became current at this machine's session start. |
| `Ectype: session data: key '<key>' is <n> bytes, over its <cap>-byte cap; left out of this session.` | Host: the value is too big. Shrink it or raise `MaxBytes`. |
| `Ectype: session data: key '<key>' (<n> bytes, the largest) would bring the envelope to <total> bytes, over the 16384-byte cap; left out of this session.` | Host: the keys together are too big; the largest goes first. |
| `Ectype: session data: producing key '<key>' threw; left out of this session: <exception>` | Host: the producer threw. |
| `Ectype: session data: applying key '<key>' threw: <exception>` | Any machine: the `apply` callback threw. Other keys still applied. |
| `Ectype: SessionData.Changed subscriber <Type>.<method> threw: <exception>` | A `Changed` subscriber threw. The others still ran. |
| `Ectype: session data: key '<key>' from the host does not read as <Type> within <cap> bytes; this client keeps no value for it.` | The host sent a value of another shape, usually another version of the mod. Logged once per key. |
| `Ectype: session data: ignored the host's envelope: <problem>.` | The envelope is over 16384 bytes, not an object, or in a format this framework does not read. |
| `Ectype: session data: ignored the session data <live from actor <s>\|from the room cache>: <why> (master is actor <m>).` | The envelope's sender or stamp is not the host (see "Who may send it"). The game still loads that session; the framework keeps no values from it. |
| `Ectype: session data: reading the envelope from the session JSON failed: <exception>` | Reading threw. The session gets no values. |
| `Ectype: session data: adding the envelope to the session JSON failed: <exception>` | Host: writing the envelope threw. Clients get no values for this session. |
| `Ectype: session data: activating the session's values failed: <exception>` | Capturing or applying at the session start threw. |
| `Ectype: session data: this run is saved under session <id>, not <id> (the host changed); its sidecar follows the saved id.` | Logged once per saved id, on a host who took over mid-run. |
| `Ectype: session data: the saved run's value for key '<key>' does not read as <Type>; a fresh value is sent.` | Host, on resume: the sidecar holds a value of another shape, usually from another version of the mod. |
| `Ectype: session data: saved-run sidecar for session <id> unreadable; values are produced fresh: <message>` | The sidecar exists and could not be read. |
| `Ectype: session data: saved-run sidecar write failed (<path>): <message>` | The config folder is not writable. A resumed run then gets fresh values. |
| `Ectype: session data: refreshing the saved-run sidecar failed: <exception>` | Writing the sidecar at a save threw. |
| `Ectype: session data: pruning saved-run sidecars failed: <message>` | Old sidecars stay until the next write. |
| `Ectype: session data: saved-run sidecar written for session <id>.` and `Ectype: session data: pruned <n> old saved-run sidecar(s).` | Verbose. |
| `Ectype: host value '<key>' refused: <problem>; it follows the local config only.` | `T` is not a bool, number, enum or string, or the key does not follow the [identifier rule](../identifiers.md). |
| `Ectype: host value '<key>' is already bound to another config entry; this one follows the local config only.` | Two `HostValue` calls named one key for different entries. Give one an explicit key. |
| `Ectype: host value '<key>' could not be serialised; not published in the hub: <message>` | Host: the config value did not convert to JSON. |
| `Ectype: host value '<key>' is over the 4096-byte cap; not published in the hub.` | Host: the value is too big for the room property. Clients read it from the next session's data. |
| `Ectype: host value '<key>' (<n> bytes, the largest) left out of the hub room property: it would bring it to <total> bytes, over the 16384-byte cap.` | Host: the values together are too big. |
| `Ectype: host value '<key>': the host's room value does not read as <Type>: <message>` | The host runs another version of the mod. |
| `Ectype: host value '<key>' Changed subscriber <Type>.<method> threw: <exception>` | A `HostValue.Changed` subscriber threw. The others still ran. |
| `Ectype: host values: ignored a room-property write from actor <a>, who is not the master client (<m>).` | Client: another player wrote the host's room property. Logged once per writer. |
| `Ectype: host values: actor <a> wrote the room property; republishing the host's values.` | Host: the same event, seen from the host. |
| `Ectype: host values: update failed (reported once): <exception>` and `Ectype: host value writer check failed (reported once): <exception>` | The hub channel threw. Host values then follow the last accepted values or the local config. |
| `Ectype: host values published to the room[ (the room held another value)].` | Verbose. The host wrote its values. |
| `Ectype: host values: room property not used (<reason>).` | Verbose. The property was absent, stamped by another actor, over the cap, not JSON or not an object. |

## Checking it in game

With two clients and `VerboseLogging` on both:

1. Host in the hub, change the setting in the host's config: the client logs the new value
   from `Room`.
2. Launch a mission: both log `Session` with the host's value; the host logs `sending`, the
   client `received`, both `applied`.
3. Join a third client mid-mission: it logs `received` with `from the room cache` and the same
   id, and reads the host's value.
4. Save an endless run, quit the game, change the host's config, start the game and resume the
   run: the host logs `restored from the saved run`, and clients read the value the run started
   with.
5. Let the host leave mid-run; the new host saves and resumes: it logs `this run is saved under
   session` once, then `restored from the saved run`.

Sample: [examples/session-data](../../examples/session-data/).
