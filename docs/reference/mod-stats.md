# Mod-declared stats and the session collection

Three things share one mechanism here:

- **Stats a mod declares in a data file.** A mod with no role ships a JSON file naming the stats
  it owns. The framework gives each one a tooltip entry and registers it into every collection
  of the declared kind. The mod itself needs no code that calls the framework.
- **Generic framework factors.** `MeleeDamage`, `MeleeCooldown`, `MeleeReach`,
  `AntiPersonnelDamage` and `HostileSpawnScale` are framework-owned multipliers of authored
  value 1. The framework registers them and reads none of them. The mod that owns the action
  multiplies its own constant by the composed value.
- **A run-wide session collection.** A stat collection that belongs to no player and no ship,
  one per session, decided by the host and delivered to every client with the session.

Every stat here is read by id from the game's own `StatCollection`. The id is a pure function of
two strings, so a mod that never references the framework computes it and reads the stat with
the game's API alone.

## Declaring stats in a data file

A stats file is JSON whose resource name or file name ends in `ectype-stats.json`, for example
`MyMod.ectype-stats.json`:

```json
{
  "owner": "fleet",
  "stats": [
    { "name": "HackSeconds", "kind": "field", "default": 60, "collection": "character",
      "display": "Hack Time", "tooltip": "Seconds a hack takes.", "lowerIsBetter": true },
    { "name": "HackResistance", "kind": "factor", "collection": "ship",
      "display": "Hack Resistance", "tooltip": "Multiplier on how long a hack of this ship takes." },
    { "name": "SupplyDropInterval", "kind": "factor", "collection": "session" }
  ]
}
```

| Key | Required | Meaning |
| --- | --- | --- |
| `owner` | yes | The namespace of every stat in the file. Lower-case letters, digits, `.`, `_` and `-`, starting with a letter or digit, at most 64 characters. `ectype` (the framework's own owner) and `framework` are reserved. Part of every id: renaming it derives new ids. |
| `stats` | yes | 1 to 256 stat objects. |
| `name` | yes | Letters, digits and `_`, starting with a letter, at most 64 characters. Part of the id. |
| `kind` | yes | `"field"`: an absolute number whose base is `default`; the mod reads the composed value in place of its constant. `"factor"`: a multiplier whose base is 1; the mod multiplies its constant by it. |
| `default` | for a field | The base value. A factor's is 1; any other number is refused. |
| `collection` | yes | `"character"`: every player character's own collection, the local player and every replica, registered at spawn. `"ship"`: every player ship's own collection, every replica, registered as the ship fills its stats. `"session"`: the session collection below. |
| `display` | no | The tooltip header. Default: the name. |
| `tooltip` | no | The tooltip body. |
| `lowerIsBetter` | no | True colours a reduction as a buff in tooltips. Default false. |
| `displayType` | no | `"value"` or `"percentage"`: how a modifier on it reads in tooltips. Default `"percentage"` for a factor, `"value"` for a field. |
| `iconFrom` | no | A vanilla `StatType` name or framework stat name whose tooltip icon the stat borrows. Default: no icon. |

An unknown key is a warning and is ignored. `$schema` and `$comment` are accepted anywhere a key
is. The JSON schema is [../schemas/ectype-stats.schema.json](../schemas/ectype-stats.schema.json).

The suffix is `ectype-stats.json` rather than `.stats.json` because the framework reads every
matching resource of every plugin and every matching file beside every plugin DLL. A generic
suffix would make it parse, and refuse, other mods' unrelated files.

### Where the framework looks, and in what order

Discovery runs once per launch, after the game's stat table has loaded and before any role
registers a perk, so a perk can name a declared stat. It reads:

1. Embedded resources of every loaded BepInEx plugin assembly whose name ends in the suffix,
   ordered by assembly name, then resource name.
2. Files ending in the suffix in the folder of every loaded plugin DLL, top level only, ordered
   by path. A folder that holds several plugins is read once.

The first declaration of an id wins. A later declaration of the same id, or of the same
`owner/name` with different capitals, is refused with one error naming both declarers. Ship the
file either embedded or loose, not both: shipping both declares every stat twice, and the loose
copy is refused. A file over 64 KB is refused. A file added while the game runs is read at the
next launch.

### What a declared stat gets

- A tooltip entry in the game's stat table under its id, so a modifier on it renders by name.
- A `ModifiableFloat` at its base, registered into every collection of its kind that does not
  already hold the id. The first collection built triggers discovery on demand, so a character
  that spawns before the framework's own registration pass still gets them.
- A name manifests resolve. A perk effect's `"stat"` names it as `"owner/Name"`, for example
  `"fleet/HackSeconds"`. The bare name does not resolve.

The framework never reads a declared stat. The declaring mod reads it; until it does, the stat
renders and changes nothing.

## Stat ids

```
id = 0x20000000 | (LE32(MD5(UTF-8("ectype:stat:" + owner + "/" + name))[0..4]) & 0x0FFFFFFF)
```

Take the MD5 of the UTF-8 string `ectype:stat:<owner>/<name>`, read its first four bytes as a
little-endian 32-bit integer, keep the low 28 bits and set bit 29. The hash is case-sensitive:
`Fleet/HackSeconds` derives a different id from `fleet/HackSeconds`. Bit 29 is set by no vanilla
`StatType`, so a derived id never equals a vanilla one.

Worked example, `fleet/HackSeconds`:

| Step | Value |
| --- | --- |
| String | `ectype:stat:fleet/HackSeconds` |
| MD5 | `d70198dac932afa77a0f9a4125623877` |
| First four bytes | `d7 01 98 da` |
| Little-endian | `0xda9801d7` |
| `& 0x0FFFFFFF` | `0x0a9801d7` |
| `\| 0x20000000` | `0x2a9801d7` = **714605015** |

The framework's own stats use owner `ectype`: `ectype/MeleeDamage` is MD5 `66d86f20...`, so
`0x206fd866` = 544200806.

Reading a stat by id without referencing the framework:

```csharp
using System;
using System.Security.Cryptography;
using System.Text;
using Gameplay.Utilities;

static class StatIds
{
    public static int Id(string owner, string name)
    {
        using (MD5 md5 = MD5.Create())
        {
            byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes("ectype:stat:" + owner + "/" + name));
            return 0x20000000 | (BitConverter.ToInt32(h, 0) & 0x0FFFFFFF);
        }
    }

    /// The composed value, or the fallback when the collection does not hold the stat
    /// (the framework is absent, or has not registered it there).
    public static float Read(StatCollection c, int id, float fallback)
    {
        StatBase s;
        if (c == null || c.Stats == null || !c.Stats.TryGetValue(id, out s)) return fallback;
        ModifiableFloat f = s.Data as ModifiableFloat;
        return f != null ? f.Value : fallback;
    }
}
```

`BitConverter.ToInt32` reads in the machine's byte order, which is little-endian on every machine
the game runs on. Compute ids
once and cache them. Use the fallback for a factor as 1 and for a field as the mod's own
constant, so the mod behaves the same with and without the framework.

## Generic framework factors

| Stat | Id | Collection | Lower is better | A reader multiplies |
| --- | --- | --- | --- | --- |
| `MeleeDamage` | 544200806 | the player character's own | no | its damage per melee hit |
| `MeleeCooldown` | 578599244 | the player character's own | yes | its seconds between melee hits |
| `MeleeReach` | 650876603 | the player character's own | no | its melee reach in metres |
| `AntiPersonnelDamage` | 568581206 | the player ship's own | no | the damage the ship's weapons deal to characters |
| `HostileSpawnScale` | 696758852 | the session collection | yes | hostile spawn group amounts |

All five are factors of authored value 1, so "no contributor" and "not registered" read the same
and a reader keeps its own constant as the base. Two readers with different bases compose
correctly. A manifest names them by bare name, like every framework stat; code gets the id from
`FrameworkStats.Id(name)` or from the formula with owner `ectype`.

How a reader uses them, with the attacker's replica as the source of truth:

- **A punch.** The attacker's client sends `damage × MeleeDamage` read from its own character.
  The victim's client clamps what it accepts to `damage × MeleeDamage` read from the attacker's
  replica, not to the constant, because every client applies every character's perks to that
  character's replica. A rate limit on the victim divides its budget by the attacker's
  `MeleeCooldown`; a range check scales its tolerance by the attacker's `MeleeReach`.
- **Turret damage to players.** The host multiplies its per-hit or per-second constant by the
  firing ship's `AntiPersonnelDamage`.
- **Spawn scaling.** The host multiplies its spawn group amount by `HostileSpawnScale` from the
  session collection.

## The session collection

`SessionStats.Collection` is a `StatTagCollection` the framework owns, one per session, the hub
included. It holds every stat declared for the `session` collection: `HostileSpawnScale` and any
stats file's.

### Lifecycle

- **Host.** As the host starts a session, at the moment session data produces its values, the
  framework builds a fresh collection with every session stat at its base, raises
  `SessionStats.Opening`, and publishes the composed value of every float stat in it under the
  session data key `framework/sessionStats` as `{"<id>": value}`.
- **Every other client.** When the session arrives, the framework builds its own collection and
  gives each stat one modifier that makes its composed value the host's. A stat the host
  published that this client does not declare is registered at the host's value, so a reader by
  id still gets it. A late joiner gets the values with the cached session, as it joins.
- **End.** The collection is dropped, and every modifier removed from it by source, when the next
  session arrives or the client leaves the room.
- **A session that carried no values** (the host's values were left out of the session data)
  opens a collection at the declared bases, with one warning.
- **A resumed run** recomposes from the contributions present at resume. The key does not keep
  the value the run started with: the collection is the host's truth, and a frozen number that no
  longer matches the host's own collection would split host and clients.

### Contributing

Only the host contributes. Subscribe at plugin `Awake`, on every client:

```csharp
SessionStats.Opening += () =>
{
    int id = FrameworkStats.Id(FrameworkStats.HostileSpawnScale);
    SessionStats.Contribute(id, new FloatModifier(enemyScale.Value - 1f, ModifierType.AdditiveMultiplier), MySource);
};
```

- `Opening` runs on the host only, inside the game's session start, before the values are
  published. A contribution made inside it reaches every client. Keep it short.
- `Contribute(statId, modifier, source)` adds one modifier. `source` is required; `Withdraw(source)`
  removes everything it added.
- A contribution after the session started changes the host's value only, with one warning per
  stat. Clients keep the session-start value until the next session.
- On a client `Contribute` returns false with one warning. Outside a session, or for an id that is
  not a session stat, it returns false with a warning.
- A mod may also `RegisterStat` its own float into the collection during `Opening`; the host
  publishes it with the rest, and clients register it at the host's value.
- The holder carries no tags, so a modifier with tag requirements never activates in the session
  collection.

**Host migration.** The new master keeps the values it received. Its later contributions change
its own collection only, as above, until it starts the next session.

**Mutators.** A mutator effect that contributes is not built. The seam is `Opening`: such an
effect would contribute from it, reading the session's active mutators, because the values are
published at that moment.

### Why there is no live path

Values contributed after the session starts do not reach clients. The session data cannot be
sent again within a session: raising the game's session event a second time makes every client
unload and reload the session. A live path would need a second channel, a room property the host
rewrites on every change and a new master republishes, and every client would then hold two
sources for one number. The first consumers of session stats (spawn scaling, supply drops) run
on the host only, where the live value is correct. A client-side reader of a value that changes
mid-session is the trigger for adding one.

### Reading the session collection without the framework

The collection's holder is a component implementing the game's `IStatCollectionHolder`, on a
GameObject named `Ectype.SessionStats` kept across scene loads:

```csharp
GameObject go = GameObject.Find("Ectype.SessionStats");
IStatCollectionHolder holder = go != null ? go.GetComponent<IStatCollectionHolder>() : null;
StatCollection sessionStats = holder != null ? holder.Stats : null;   // null outside a session
float scale = StatIds.Read(sessionStats, StatIds.Id("ectype", "HostileSpawnScale"), 1f);
```

`StatTagCollection`'s constructor reads its holder's tags, so the collection needs a holder. No
game code reads a holder's transform, and the game looks a holder up only by walking a
transform's parents or root, so a GameObject at the scene root is never mistaken for part of a
ship.

## Reading through the framework

- `ModStats.Id(owner, name)`: the formula. `ModStats.Id(name)` takes `"owner/Name"` (a declared
  name is matched ignoring case), `"ectype/Name"` or a bare framework name.
- `ModStats.TryGet(collection or kind, name, out value)` and `ModStats.Get(collection or kind, name)`:
  the composed value. When the collection does not hold the stat, the value is the declared base
  (1 for a factor), or 0 for a name with no declaration.
- `ModStats.LocalCollection(kind)`: the local player's character collection, the local player's
  crew ship's, or the session collection.
- `ModStats.IsDeclared("owner/Name")`.

## Log lines

| Line | Meaning |
| --- | --- |
| `Ectype: mod stats: <n> declared in <m> file(s): <owner/name>=<id> (<collection>), ...` | Discovery registered these. |
| `Ectype: mod stats: none declared (<m> file(s) read).` | Files were found and none of their stats registered; the errors above say why. |
| `Ectype: mod stats: <declarer> rejected: <problem>.` | The whole file was refused. |
| `Ectype: mod stats: <declarer> stat #<i> ('<name>') skipped: <problem>.` | One stat was refused; the rest of the file stands. |
| `Ectype: mod stats: <declarer> declares '<owner/name>' (id <id>), already declared as '<owner/name>' (id <id>) by <declarer>; the later declaration is skipped.` | A duplicate. |
| `Ectype: mod stats: <character\|ship\|session> surface installed on <type> (<owner/name>=<base>, ...).` | The first collection of that kind received the stats. Logged once per kind. |
| `Ectype: session stats: opened for session <id> <as host\|with the host's values\|at the declared defaults> (<name>=<value>, ...).` | A session collection opened with these values. |
| `Ectype: session stats: <name> changed after the session started; clients keep the session-start value until the next session.` | A contribution outside `Opening`. |

[../troubleshooting.md](../troubleshooting.md) lists every line with its cause.

## Things that look like they should work

- **A file named `MyMod.stats.json`.** Not read: the name must end in `ectype-stats.json`.
- **A stats file in a subfolder of the plugin's folder.** Not read: only the DLL's own folder.
- **Owner `Fleet`.** Refused: owners are lower case, because the id hash is case-sensitive and a
  reader computing the id must spell the owner exactly as declared.
- **`"stat": "HackSeconds"` in a manifest.** Unresolved: a declared stat is named
  `"owner/Name"`.
- **Computing the id from `fleet/hackseconds`.** A different id. The framework's name lookup
  ignores case; the formula does not.
- **A factor with `"default": 2`.** Refused. Declare a field when the base is not 1.
- **Contributing to a session stat from a client.** Refused. Clients receive the host's value.
- **Contributing mid-session and expecting clients to see it.** They see it from the next
  session.
- **A tag-filtered modifier in the session collection.** Never active: the collection has no
  tags.
- **Reading the session collection in the main menu.** Null: it exists only inside a session.

Sample: [examples/mod-stats](../../examples/mod-stats/).
