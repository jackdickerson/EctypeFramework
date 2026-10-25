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
  one per session, decided by the host and delivered to every client with the session. It takes
  contributions from code on the host and from mutators in the game's Mutators menu.

Every stat here is read by id from the game's own `StatCollection`. The id is a pure function of
two strings, so a mod that never references the framework computes it and reads the stat with
the game's API alone.

## Declaring stats in a data file

A stats file is JSON whose resource name or file name ends in `ectype-stats.json`, for example
`MyMod.ectype-stats.json`:

```json
{
  "owner": "com.captainchunck.voidcrew.fleet",
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
| `owner` | yes | The namespace of every stat in the file: the declaring plugin's BepInEx GUID, spelled exactly as its `[BepInPlugin]` attribute declares it, capitals included (the [identifier rule](../identifiers.md)). Letters, digits, `.`, `_` and `-`, at most 128 characters. An embedded file names a plugin its assembly declares; a loose file names a plugin whose DLL is in its folder. `ectype` (the framework's own owner) and `framework` are reserved, and so is the id of every installed role: a role's stats come from its manifest's `customStats`. Part of every id: renaming it derives new ids. |
| `stats` | yes | 1 to 256 stat objects. |
| `name` | yes | Letters, digits and `_`, starting with a letter, at most 64 characters. Part of the id. |
| `kind` | yes | `"field"`: an absolute number whose base is `default`; the mod reads the composed value in place of its constant. `"factor"`: a multiplier whose base is 1; the mod multiplies its constant by it. |
| `default` | for a field | The base value, a JSON number. A factor's is exactly 1; any other number refuses the stat. A number no 32-bit float can hold (beyond about ±3.4e38) refuses the whole file. |
| `collection` | yes | `"character"`: every player character's own collection, the local player and every replica, registered at spawn. `"ship"`: every player ship's own collection, every replica, registered as the ship fills its stats. `"session"`: the session collection below, which holds at most 128 stats. |
| `display` | no | The tooltip header. Default: the name. |
| `tooltip` | no | The tooltip body. |
| `lowerIsBetter` | no | True colours a reduction as a buff in tooltips. Default false. |
| `displayType` | no | `"value"` or `"percentage"`: how a modifier on it reads in tooltips. Default `"percentage"` for a factor, `"value"` for a field. |
| `iconFrom` | no | A vanilla `StatType` name or framework stat name whose tooltip icon the stat borrows. Default: no icon. |

`display`, `tooltip`, `iconFrom` and `displayType` must be strings and `lowerIsBetter` a boolean;
a value of another type, `null` included, refuses the stat. `$schema` and `$comment` at the top
level, and `$comment` in a stat, are accepted silently. Any other key is a warning to the
framework and is ignored; the JSON schema,
[../schemas/ectype-stats.schema.json](../schemas/ectype-stats.schema.json), refuses it. The
schema and the framework agree on every other rule.

The suffix is `ectype-stats.json` rather than `.stats.json` because the framework reads every
matching resource of every plugin and every matching file beside every plugin DLL. A generic
suffix would make it parse, and refuse, other mods' unrelated files.

### Where the framework looks, and in what order

Discovery runs once per launch, after the game's stat table has loaded and before any role
registers a perk, so a perk can name a declared stat. It reads:

1. Embedded resources of every loaded BepInEx plugin assembly whose name ends in the suffix.
2. Files ending in the suffix in the folder of every loaded plugin DLL, top level only. A folder
   that holds several plugins is read once.

A plugin that failed to load contributes neither. Each file is parsed on its own: a file that
fails, for any reason, is refused by name and every other file still registers. A file whose
owner is not the GUID of one of its own plugins, those its assembly declares for an embedded
file and those whose DLL shares its folder for a loose one, is refused with an error naming the
GUIDs it could have used. A file over
64 KB is refused. A file added while the game runs is read at the next launch.

When two declarations share an id, or an `owner/name` up to capitals, one wins and the other is
refused with one error naming both declarers. The winner is decided the same way on every
machine, whatever folders the mod manager installed into: an embedded declaration before a loose
one, then the declaring assembly's simple name (for a loose file, the smallest among the plugins
whose folder holds it), then the resource or file name. Paths never decide. Ship the file
either embedded or loose, not both: shipping both declares every stat twice, and the loose copy
is refused.

### What a declared stat gets

- A tooltip entry in the game's stat table under its id, so a modifier on it renders by name.
- A `ModifiableFloat` at its base, registered into every collection of its kind that does not
  already hold the id. The first collection built triggers discovery on demand, so a character
  that spawns before the framework's own registration pass still gets them.
- For a `character` or `ship` stat, a name manifests resolve. A perk effect's `"stat"` names it
  as `"owner/Name"`, for example `"com.captainchunck.voidcrew.fleet/HackSeconds"`. The bare name
  does not resolve. A manifest effect naming a `session` stat is refused, because no perk target
  reaches the session collection.

The framework never reads a declared stat. The declaring mod reads it; until it does, the stat
renders and changes nothing.

## Stat ids

```
id = 0x20000000 | (LE32(MD5(UTF-8("ectype:stat:" + owner + "/" + name))[0..4]) & 0x0FFFFFFF)
```

Take the MD5 of the UTF-8 string `ectype:stat:<owner>/<name>`, read its first four bytes as a
little-endian 32-bit integer, keep the low 28 bits and set bit 29. The hash is case-sensitive:
`Com.Example.MyMod/HackSeconds` derives a different id from `com.example.mymod/HackSeconds`. Bit
29 is set by no vanilla `StatType`, so a derived id never equals a vanilla one.

Worked example, `com.captainchunck.voidcrew.fleet/HackSeconds`:

| Step | Value |
| --- | --- |
| String | `ectype:stat:com.captainchunck.voidcrew.fleet/HackSeconds` |
| MD5 | `8c33a513dd3d46af3772f213d73a42e7` |
| First four bytes | `8c 33 a5 13` |
| Little-endian | `0x13a5338c` |
| `& 0x0FFFFFFF` | `0x03a5338c` |
| `\| 0x20000000` | `0x23a5338c` = **598029196** |

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
`FrameworkStats.Id(name)` or from the formula with owner `ectype`. `HostileSpawnScale` lives in
the session collection, so a manifest effect naming it is refused like any session stat.

How a reader uses them, with the attacker's replica as the source of truth:

- **A punch.** The attacker's client sends `damage × MeleeDamage` read from its own character.
  The victim's client clamps what it accepts to `damage × MeleeDamage` read from the attacker's
  replica, not to the constant, because every client applies every character's perks to that
  character's replica. A rate limit on the victim divides its budget by the attacker's
  `MeleeCooldown`; a range check scales its tolerance by the attacker's `MeleeReach`. A punch
  sent as a [character status request](character-statuses.md#limits-that-follow-the-requester)
  does all three through `MaxRangeFor`, `MinIntervalFor` and `MaxMagnitudeFor`.
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
  framework drops the previous session's collection, builds a fresh one with every session stat
  at its base, contributes the effects of the session-stat mutators the session is about to
  activate (see [Contributions from mutators](#contributions-from-mutators)), raises
  `SessionStats.Opening`, and publishes the composed value of every float stat in it under the
  session data key `framework/sessionStats` as `{"<id>": value}`. Inside `Opening`,
  `SessionStats.Collection` is the new collection and `SessionStats.SessionId` is null. This
  happens before the quest is generated and before the first sector exists, so a reader in the
  first sector already sees the new values.
- **Every other client.** When session data applies the host's values, at the same point of the
  client's own session start, the framework builds its own collection and
  sets each stat's base to the host's value, which reproduces it exactly. A stat the host
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

Session stats take contributions from two places: `SessionStats.Contribute` on the host, and a
mutator registered through `SessionStatMutators` (next section). No perk target reaches the
session collection, and a perk effect naming a session stat is refused. To contribute from code,
subscribe at plugin `Awake`, on every client:

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
- A mod may also `RegisterStat` its own float into `SessionStats.Collection` during `Opening`;
  the host publishes it with the rest, and clients register it at the host's value.
- The holder carries no tags, so a modifier with tag requirements never activates in the session
  collection.

**Host migration.** The new master keeps the values it received. Its later contributions change
its own collection only, as above, until it starts the next session.

### Contributions from mutators

A mutator is the game's own layer for a rule that applies to a whole run. It is a card in the
game's Mutators menu that the crew selects before a run, and a quest asset can force it on
through its `ForcedMutators` list. `SessionStatMutators.Register(MutatorSpec)` registers a
mutator whose rule is a list of session-stat modifiers. While a run has it active, those
modifiers are in the session collection and in the values every client receives.

```csharp
MutatorSpec spec = new MutatorSpec
{
    Owner = PLUGIN_GUID,
    Key = "hostile-surge",
    DisplayName = "Hostile Surge",
    Description = "Hostile spawn groups are half again as large.",
};
spec.Effects.Add(new SessionStatEffect
{
    Stat = "ectype/HostileSpawnScale", Kind = SessionStatModifierKind.Factor, Amount = 1.5f,
});
MutatorHandle surge = SessionStatMutators.Register(spec);   // from Awake
```

A role manifest declares the same thing in its `mutators` block; see
[../manifest.md](../manifest.md#mutatorspec).

| `MutatorSpec` field | Meaning |
| --- | --- |
| `Owner` | Namespace of the key: the registering plugin's BepInEx GUID (the [identifier rule](../identifiers.md)). A manifest's mutators are owned by its `roleId`. |
| `Key` | Stable key within the owner: letters, digits, `_`, `.` and `-`, starting with a letter or digit, at most 64 characters. |
| `Guid` | Optional explicit GUID, 32 hex digits with dashes allowed. Absent: derived, see below. |
| `DisplayName` | The card's title. Required. |
| `Description` | The card's body. Rich text is allowed. |
| `Icon` | The card's icon, a `Sprite`. Optional. |
| `IconDonor` | A registered mutator's GUID whose icon the card borrows when `Icon` is null. Without either, the card shows the framework's placeholder. |
| `Effects` | 1 to 16 `SessionStatEffect`s: `Stat`, `Kind`, `Amount`. |

`Stat` names a session stat: `"owner/Name"` for a stats file's stat, or a framework stat as
`"ectype/Name"` or its bare name. `Kind` decides how `Amount` composes:

| Kind | Amount | The game's modifier |
| --- | --- | --- |
| `Flat` (`"flat"`) | Added to the base. | `PrimaryAddend`, amount as given. |
| `Percent` (`"percent"`) | 0.25 is +25%. | `AdditiveMultiplier`, amount as given. |
| `Factor` (`"factor"`) | 1.5 is x1.5. Above 0. | `ExponentialMultiplier`, amount `factor - 1`. |

The game composes a stat as `(base + flats) × (1 + percents) × factor × factor ...`. Percents add
to each other before they multiply. Factors multiply each other, so two mutators each with a
factor of 1.5 on one stat give 2.25.

The GUID, when not given, is the 32 lower-case hex digits of MD5 over the UTF-8 string
`ectype:mutator:<owner>/<key>`. For owner `com.captainchunck.voidcrew.fleet` and key `pilgrimage`
that is `b0a920957ba0011396ae1e483f09c19d`. A saved run records its mutators by GUID, so renaming the owner
or the key orphans the mutator in every saved run that used it. The run then loads without it
(see [mutator-safety.md](mutator-safety.md)).

#### Registration

`Register` checks the spec at once, in this order: owner (the GUID of a loaded plugin, which a
plugin's own GUID is from its `Awake` on), key, GUID, name, icon donor, effects
(count, then each effect's stat, kind and amount). A refused spec returns a handle whose
`State` is `Failed` with a `Reason`, and logs one error. Registering the same owner and key again
returns the first handle. Another spec under a GUID already registered is refused.

An accepted spec is `Pending` until the game's asset tables exist and the stats files have been
read. Then each effect's stat must resolve to a session stat, or the mutator is refused. The
framework registers pending mutators in GUID order. Each registration is this walk, every write
skipped when the game already holds this registration's own object:

1. A `Mutator` asset under the GUID, carrying one `SessionStatMutatorEffect` with the resolved
   modifiers. The game's activation iterates `Effects` without a null check, so the list is never
   null.
2. Its card (`ContextInfo`), its `MutatorDef` and its unlock def, whose references are marked
   runtime.
3. The claim, before anything is written to the game's tables. The runtime-asset register must
   hold nothing under the GUID, or this registration's own `Mutator`. Until this registration has
   first held the GUID, the mutator container and the unlock container must hold nothing under
   it, or this registration's own defs. Otherwise the mutator is refused and the other holder's
   entries stay as they were. A def whose asset resolves to another object would never verify,
   and the Mutators menu dereferences every listed row's asset while it builds, so one such row
   leaves every row of the menu without its click handlers.
4. An entry in the game's runtime-asset register with the Required effect.
5. An entry in the mutator container, and the def in the container's authored list, placed among
   the runtime entries in GUID order. The Mutators menu lists only the authored list. The
   container's runtime list is then put in the authored list's order: the shared unlocks below
   name an entry by its index in the authored list and read it back by index in the runtime list.
6. An unlock entry with a manual criterion, in the unlock container and its authored list. The
   menu reads every row's unlock entry while it refreshes, and one missing entry blanks every row.
7. A walk back: the container resolves the GUID, the def's asset resolves and carries the effect
   and the GUID, the card has a title, the def is in the authored list, the unlock entry exists.

A failed attempt is retried on the next registration pass, about every 2 seconds, up to 200
attempts. A mutator refused for good, by a refusal or at its 200th attempt, is taken out of the
runtime-asset register, the mutator container and its authored list, and the unlock container
and its authored list, wherever the entry is its own: the menu never lists a def that did not
verify, and no hosted room requires it. The game has no removal call for these tables, so the
framework removes the entries from their private lookups by name. Every 10 seconds the walk back
runs again on every registered mutator. One that no longer holds, because the game rebuilt a
container, is registered again. Before each session start, every pending mutator gets an attempt
and every registered one is walked back, because the host snapshots its Required assets and its
starting mutators right after.

**One order on every machine.** The shared unlocks, and the room property the matchmaking list
reads a room's mutators from, name a mutator by its index in the authored list, so every client must build that list in the same
order. The framework inserts each def into the runtime tail of the list: the entries after the
last one whose reference is not marked runtime. A tail built only by that insert is sorted by
GUID whatever order the inserts ran in. This holds while every entry another mod adds to the
mutator container's authored list carries a reference marked runtime. An entry whose reference
is not marked runtime ends the tail where it lands, so framework mutators registered before it
sit ahead of it and those registered after it sit behind. Registration timing then decides the
indices, and two clients with the same mods can disagree on them.

The menu draws a row as selectable only when its index is in the union, across the room's
players, of a player property holding each player's unlocked mutator indices. The game writes
that property in full when the player's profile initialises, from the unlock check, which the
framework answers as unlocked for its mutators. The profile can initialise before the
registration, and it rebuilds the property on every profile rebuild, so every 2 seconds the
framework reads the local player's property back and writes every missing index in one write.
In a room, the local copy of a player property changes only when the server echoes the write
back, so a second write sent before that echo would start from the stale copy and drop the
first one's indices. After a write, the framework writes nothing until every index it added
reads back or 5 seconds have passed, and only an index still missing then counts as a failed
attempt; after 10, that mutator stops being written until its index shows up. Nothing is
unlocked in the player's profile. An index past 255 cannot be written, and that mutator shows
locked.

**The join cost.** Every framework mutator is registered with the Required effect. The game
compares the Required assets of host and joiner at join, so a player without the registering
mod is refused at the join screen, which names the missing asset, in every room hosted with
it, whether or not the run uses the mutator. The same set of Required assets marks every hosted
room as modded. A framework build made with `-p:NoProgressionFlag=true` registers with no effect:
the mutator is then left out of the join comparison. A player who has the framework and not the
mod joins, the framework drops the mutator from that player's starting list, and that player
still reads the host's session values, effects included.

#### When the effects apply

The game starts a hosted session in this order:

1. The previous session ends. Its mutators are deactivated.
2. The new session object becomes active. Its starting list holds the mutators selected in
   the menu, or the saved run's list on resume. The framework first attempts its pending
   registrations and re-verifies the registered ones. Then session data captures the session's values: the session collection opens,
   the mutator effects are contributed, `Opening` is raised, and the values are composed.
3. The quest is generated, and the game activates the quest's forced mutators.
4. The first sector is entered.
5. The session is sent to the room, cached for late joiners, with the values captured at step 2.
6. The game activates the starting list.

No mutator is active when the values are captured at step 2, and the menu-selected ones
activate only after the session has been sent. A value read from the game's active mutators at
capture time would miss all of them, and contributing at activation would reach the host only.
So, at step 2, the framework works out which mutators the game will activate, from the same
inputs and by the same rules the game uses:

- The quest asset's `ForcedMutators`, in order. A resumed run's quest asset is the saved one.
- Then the session's starting list, skipping a GUID already taken, one the quest's whitelist
  leaves out when the quest uses one (`UseMutatorWhitelist`), and one this client's mutator
  container does not hold.
- The hub has no quest: only its starting list counts.

Every `SessionStatMutatorEffect` on each of those mutators is contributed, with the mutator itself
as the source. After step 6 the host
compares this set with the game's active mutators and logs an error for any mutator it
contributed that the game did not activate.

A mutator the game activates any other way, such as a mod calling the game's `AddMutator` later
in the session, contributes when it activates. That reaches the host's collection only, with a
warning, like any contribution made after the session started.

Every other client applies the host's values at step 2 of its own start, as for any session
stat. No mutator code contributes on a client. A late joiner gets the values with the cached
session.

A resumed run recomposes from its saved starting list and quest, so it carries the same mutator
contributions as when it was saved, together with whatever the code contributors add at resume.

A mod may put a `SessionStatMutatorEffect` in a `Mutator` asset it registers itself, with the stat
ids resolved. It is contributed the same way as one the framework registered.

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
| `Ectype: mutators: '<name>' registered (<guid>, effect Required): <stat> <amount>, ...` | A session-stat mutator registered. Amounts read `+2`, `+25%` or `x1.5`. |
| `Ectype: mutators: <name>, ... contributed <n> modifier(s) to the session stats.` | Host, at session start: the mutators the session is about to activate contributed. |
| `Ectype: mutators: role '<roleId>' failed to register; its <n> mutator(s) are not registered.` and `Ectype: mutators: role '<roleId>' is suppressed by another role; its <n> mutator(s) are not registered.` | A role manifest's `mutators` register only with the role. |
| `Ectype: mutators: '<name>' could not be taken out of <tables> after it was refused (game update?); its entry stays there.` | A refused mutator's entries could not all be removed; the menu may still list it. |

[../troubleshooting.md](../troubleshooting.md) lists every line with its cause.

## Things that look like they should work

- **A file named `MyMod.stats.json`.** Not read: the name must end in `ectype-stats.json`.
- **A stats file in a subfolder of the plugin's folder.** Not read: only the DLL's own folder.
- **Owner `fleet`.** Refused: a short name is not a loaded plugin's GUID. The owner is the
  plugin's GUID, `com.captainchunck.voidcrew.fleet`.
- **The plugin's GUID in other capitals.** Refused: the owner must be spelled exactly as the
  plugin declares it, because the id hash is case-sensitive and a reader computing the id spells
  the owner as declared.
- **A loose stats file in a shared folder, owned by a plugin in another folder.** Refused: a
  loose file names a plugin whose DLL is in its own folder.
- **`"stat": "HackSeconds"` in a manifest.** Unresolved: a declared stat is named
  `"owner/Name"`.
- **A perk effect on `HostileSpawnScale` or another session stat.** Refused: no perk target
  reaches the session collection. Contribute from `SessionStats.Opening` on the host.
- **A stats file whose owner is a role's id.** Refused: a role declares its stats in its
  manifest's `customStats`, under the same id formula.
- **`"default": 1e40`.** The whole file is refused: no float holds it.
- **Computing the id from `com.captainchunck.voidcrew.fleet/hackseconds`.** A different id. The
  framework's name lookup ignores case; the formula does not.
- **A factor with `"default": 2`.** Refused. Declare a field when the base is not 1.
- **Contributing to a session stat from a client.** Refused. Clients receive the host's value.
- **Contributing mid-session and expecting clients to see it.** They see it from the next
  session.
- **A tag-filtered modifier in the session collection.** Never active: the collection has no
  tags.
- **Reading the session collection in the main menu.** Null: it exists only inside a session.
- **Reading `ActiveMutators` inside `Opening` to decide a contribution.** Empty: the game activates
  no mutator before the values are captured. Register the mutator through `SessionStatMutators`,
  or read the session's starting list and its quest's forced list.
- **Contributing from a mutator effect's own activation.** Reaches the host only: the game
  activates mutators after the values are captured.
- **A mutator effect on a character or ship stat.** The mutator is refused. A mutator contributes
  to session stats only.
- **`"kind": "Factor"` in a manifest.** Refused: the kinds are lower case.
- **A factor meant as a change.** A factor of 0.5 halves the stat. +50% is a factor of 1.5 or a
  percent of 0.5. A factor of 0 is refused.
- **Selecting Hostile Surge, or any mutator on `HostileSpawnScale`, with no spawn mod installed.**
  The published value changes and nothing reads it. The framework reads none of its generic
  stats.
- **Renaming a mutator's key.** A new GUID: a saved run that used the old one loads without it.
- **An explicit `guid` copied from a vanilla mutator, another mod's mutator, or any asset the
  game's runtime-asset register holds.** Refused before anything is written; the other holder
  keeps its entry.
- **Adding your own `MutatorDef` to the mutator container's authored list with a reference not
  marked runtime.** It ends the runtime tail where it lands, and framework mutators then take
  indices that depend on registration timing, so the shared unlocks and a room's mutator list
  can name different mutators on different clients. Mark the reference runtime.
- **A role's `mutators` while another role suppresses it.** None of them register.

Samples: [examples/mod-stats](../../examples/mod-stats/) (stats file and reader),
[examples/framework-stats](../../examples/framework-stats/) (the Hostile Surge mutator).
