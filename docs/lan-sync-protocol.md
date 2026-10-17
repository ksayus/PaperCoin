# PaperCoin LAN Sync Protocol v1

This is the shared contract between the Android app and the Windows desktop app.
Both implementations must produce byte-identical hashes and the same merge decisions.

## Goals / non-goals

- Sync all bills between two devices on the same LAN.
- Add, edit and delete propagation, including date moves.
- Detect conflicts instead of silently overwriting; the user picks local or remote for each conflict, or cancels the whole sync.
- No cloud, no account, no relay server.
- Not a backup replacement. Not encrypted. Use only on a trusted LAN.

## Constants

| Item | Value |
|---|---|
| Protocol version | `1` |
| Discovery UDP port | `47831` |
| Preferred TCP port | `47832` (if busy, bind port 0 and advertise the actual port) |
| Framing | 4-byte big-endian length + UTF-8 JSON |
| Max frame | 8 MiB |
| Auto sync interval | 20 s |
| Discovery broadcast interval | 2 s |
| Connect / read timeout | 5 s / 30 s |
| Session hard timeout | 2 min |

## State file

Path:

- Android: `<filesDir>/PaperCoin/Sync/papercoin-sync.json`
- Windows: `%APPDATA%\PaperCoin\Sync\papercoin-sync.json`

Schema (camelCase, unknown fields ignored):

```json
{
  "enabled": false,
  "autoSync": true,
  "code": "",
  "deviceId": "6f1c7e80-2f4b-4d7a-9c17-0e6f3f9a1b2c",
  "deviceName": "Pixel 8",
  "peers": [
    {
      "deviceId": "0b7d9f11-7e2a-4a4f-9d3a-3f6a0e2c4b51",
      "deviceName": "DESKTOP",
      "lastSyncAt": 1792250400000,
      "baseline": {
        "11111111-1111-4111-8111-111111111111": "5015e4ccef8f9565d925127cc5b483b831f03bad2dab6bf0fb9482fa35f75593"
      }
    }
  ]
}
```

- `deviceId`: GUID, generated once and persisted.
- `deviceName`: 1..48 chars, user editable.
- `code`: 6..12 chars from `A-Z0-9`, stored uppercase. Empty = sync disabled.
- `peers`: known devices only. `baseline` is the map of entry id -> content hash at the last successful sync with that peer.
- `lastSyncAt`: Unix epoch milliseconds (UTC).
- `baseline` may be empty.

## Content hash

Canonical string for one entry:

```
"PC1" + 0x1F + description + 0x1F + amount + 0x1F + category + 0x1F + date
```

- `description`: exact stored string (already trimmed by the editor).
- `amount`: exactly two decimals, invariant culture, rounding half away from zero,
  e.g. `-12.50`, `12000.00`.
- `category`: `category ?? ""` then `Trim()`.
- `date`: `yyyy-MM-dd`.
- Encoding: UTF-8. Hash: SHA-256, lowercase hex.

Snapshot hash:

1. For every entry compute its content hash.
2. Sort by `id` using ordinal (UTF-16 code unit) comparison.
3. Build `id + 0x1F + hash + "\n"` for each.
4. SHA-256 of the UTF-8 bytes. Empty snapshot hashes the empty string.

### Test vectors

Entry:

- description `午饭`
- amount `-12.5`
- category `餐饮`
- date `2026-10-18`

hash = `5015e4ccef8f9565d925127cc5b483b831f03bad2dab6bf0fb9482fa35f75593`

Entry:

- description `地铁`
- amount `-6`
- category `交通`
- date `2026-10-18`

hash = `9e3182d6536d646424e7ca3239c1bdd201b130964500a0ca1170bd9553e5c7c9`

Snapshot containing exactly those two entries (ids in sorted order):

```
11111111-1111-4111-8111-111111111111
22222222-2222-4222-8222-222222222222
```

snapshot hash = `0f3fee08b5b1ad19bdd72eabd7b8f16df60b36f7024a4d0228b04e84aa17e6c4`

## Authentication

The sync code is never sent over the wire.

1. Client sends `hello` with `deviceId`, `deviceName`, `app`, `version`, `protocol`.
2. Server generates 16 random bytes, sends `challenge` with `nonce` = base64.
3. Client computes `HMAC-SHA256(key = UTF-8(code), message = nonceBytes + UTF-8(deviceId))`
   and sends `auth` with lowercase hex `mac`.
4. Server compares in constant time. Mismatch -> `error { code: "auth" }` and close.
5. Server sends `welcome`.

Test vector:

- code `ABCD2345`
- nonce bytes `00 01 02 ... 0F`
- nonce base64 `AAECAwQFBgcICQoLDA0ODw==`
- deviceId `device-1`
- mac = `0889e7bae7ad9f5038aad194514aeb8a3195e1c4f83f8f9abc7e70c4b7198a08`

## Wire messages

One JSON object per frame. All fields are optional unless stated. Unknown fields ignored.
Null fields may be omitted.

```json
{
  "type": "hello",
  "protocol": 1,
  "deviceId": "...",
  "deviceName": "...",
  "app": "android",
  "version": "1.3.8",
  "tcpPort": 47832,
  "nonce": "...",
  "mac": "...",
  "entries": [],
  "hash": "...",
  "expectedSnapshotHash": "...",
  "finalSnapshotHash": "...",
  "actions": [],
  "baseline": [],
  "count": 0,
  "code": "auth",
  "message": "...",
  "reason": "..."
}
```

Types:

| type | direction | purpose |
|---|---|---|
| `discover` | UDP broadcast | discovery probe |
| `offer` | UDP unicast reply | discovery answer, includes `tcpPort` |
| `hello` | client -> server | protocol + identity |
| `challenge` | server -> client | `nonce` |
| `auth` | client -> server | `mac` |
| `welcome` | server -> client | server identity |
| `snapshot` | both | `entries`, `hash` |
| `apply` | client -> server | `expectedSnapshotHash`, `finalSnapshotHash`, `actions`, `baseline` |
| `applied` | server -> client | `count`, `hash` (final snapshot hash) |
| `abort` | client -> server | `reason`; no changes |
| `error` | both | `code`, `message` |

`entries` are the same JSON objects as `bills.json`:
`id`, `description`, `amount` (number), `category` (string/null), `date` (`yyyy-MM-dd`).

Action:

```json
{ "op": "upsert", "entry": { "...": "..." } }
{ "op": "delete", "id": "...", "date": "2026-10-18" }
```

Baseline item:

```json
{ "id": "...", "hash": "..." }
```

## Discovery

UDP datagrams, UTF-8 JSON, on port 47831.

Discover:

```json
{ "type": "discover", "protocol": 1, "deviceId": "...", "deviceName": "...", "tcpPort": 47832 }
```

Offer (unicast back to the datagram source address and port):

```json
{ "type": "offer", "protocol": 1, "deviceId": "...", "deviceName": "...", "tcpPort": 47832 }
```

- Ignore datagrams whose `deviceId` equals local.
- Send to every non-loopback IPv4 interface broadcast address plus `255.255.255.255`.
- A device that discovers a peer replies with `offer`; it must not recurse.
- Known peers only are auto-synced. Unknown peers appear in the UI for a manual sync.

## Session

1. Client connects to `host:tcpPort`.
2. `hello` -> `challenge` -> `auth` -> `welcome`.
3. Client sends local `snapshot`; server replies with its `snapshot`.
4. Client computes the merge (below).
5. If unresolved conflicts remain: send `abort` with `reason: "conflict"`, close. No changes.
6. Otherwise build actions and send `apply` with:
   - `expectedSnapshotHash`: the server snapshot hash received in step 3.
   - `finalSnapshotHash`: hash of the merged final snapshot.
   - `actions`: changes that bring the server to the final snapshot.
   - `baseline`: final entries as `id -> hash`, sorted by id.
7. Server re-reads its current snapshot, verifies its hash equals `expectedSnapshotHash`.
   - mismatch -> `error { code: "snapshot-changed" }`, close; no changes.
8. Server applies actions, re-reads, verifies hash equals `finalSnapshotHash`.
   - mismatch -> `error { code: "internal" }`.
   - success -> stores/updates the peer's baseline and `lastSyncAt`, sends `applied`.
9. Client re-reads its local snapshot, verifies its hash still equals the snapshot sent in step 3
   (otherwise abort local apply), applies local actions, re-reads, verifies `finalSnapshotHash`,
   stores/updates the peer baseline and `lastSyncAt`.
10. Close.

The client applies local changes only after `applied`; a cancel never changes either side.

## Merge

Inputs:

- `L`: local snapshot (id -> entry/hash)
- `R`: remote snapshot
- `B`: baseline for this peer (id -> hash, possibly empty)
- optional `choice` map for conflict ids: `LOCAL` | `REMOTE`

For every id in union(L, R, B):

| L | R | condition | result |
|---|---|---|---|
| yes | yes | `hash(L) == hash(R)` | final = L (equal content) |
| yes | yes | differ, `L != B` and `R != B` | **conflict** |
| yes | yes | differ, only L changed | final = L |
| yes | yes | differ, only R changed | final = R |
| yes | no | no B entry | final = L (new local) |
| yes | no | `hash(L) == B` | final = absent (remote deleted, local unchanged) |
| yes | no | `hash(L) != B` | **conflict** (local changed, remote deleted) |
| no | yes | no B entry | final = R (new remote) |
| no | yes | `hash(R) == B` | final = absent (local deleted, remote unchanged) |
| no | yes | `hash(R) != B` | **conflict** (local deleted, remote changed) |
| no | no | — | final = absent (both deleted) |

`changed` means: no baseline entry for that id, or current hash differs from the baseline hash.

Conflict display:

- both present: local vs remote entry fields.
- local absent: "本机已删除" vs remote entry.
- remote absent: local entry vs "对方已删除".

Conflict resolution:

- `LOCAL` -> final = L (absent means delete).
- `REMOTE` -> final = R (absent means delete).
- No choice -> keep the conflict; do not apply anything.
- Cancel -> abort the session and discard all choices.

Actions for a side `S` (local or remote), given final snapshot `F`:

1. For every id where `S` has an entry and (`F` is absent, or `F` date differs, or `F` hash differs):
   - emit `delete(id, S.date)` when `S` date differs or `F` absent.
2. For every `F` entry where `S` is absent, or `S` date differs, or `S` hash differs:
   - emit `upsert(F.entry)`.
3. Delete actions must be emitted before upserts for the same id.

Final snapshot = all final entries. Baseline after success = id -> hash for every final entry.

## Auto sync

- Only start when `enabled == true` and `code != ""`.
- Server and discovery run while the app is in the foreground (Android) or running (Windows).
- On `offer`, if the peer is in `peers` and
  `localDeviceId.CompareTo(peerDeviceId, Ordinal) < 0`, initiate a sync.
  The other side waits; this prevents both sides connecting at once.
- Throttle automatic attempts per peer to one per 20 s.
- Unknown peers are never auto-synced; user must start a manual sync once.
- A manual sync can target a discovered peer or an explicit `host:port`.
- If conflicts are found, keep the conflict list and wait for the user. A later retry
  reconnects, exchanges fresh snapshots and applies the saved choices to matching ids;
  new conflict ids require new choices.

## Errors

| code | meaning |
|---|---|
| `auth` | sync code mismatch |
| `protocol` | unsupported protocol version |
| `busy` | another session is running |
| `snapshot-changed` | data changed between snapshot and apply; retry |
| `invalid` | malformed frame/message |
| `internal` | local storage or verification failure |

All errors are non-fatal to the app; the UI shows a readable message and keeps bills untouched
unless the error happened after a successful apply (then the next sync converges).

## Limits / privacy

- No TLS and no encryption; anyone on the LAN who can sniff traffic can read bills after a successful session.
  The HMAC only prevents a wrong-device/no-code sync; it does not hide data.
- Use only on a trusted home/office LAN. Do not expose the port to the internet.
- The sync code is low-entropy; it is a pairing guard, not a password vault.
- Android stops sync when the app leaves the foreground. Windows syncs while the app runs.
