# Novalist Sync — Design

Status: draft v0.2 · Last updated: 2026-10-18

## 1. Goal

Add end-to-end encrypted (E2EE), real-time collaborative editing to **Novalist**
(github.com/Drommedhar/novalist-official). Several users can work on the same
**project** at the same time, work offline, and share everything a project
contains: scenes, Codex entries, structure, images and research files. The sync
server stores and relays data but **can never read project content, file
content, or keys**.

### Non-goals (v1)

- **Hiding metadata** from the server (who works on which project, when, how
  much, how many items).
- **Full forward secrecy.** For a persistent shared document it largely doesn't
  apply: past edits _are_ the current document, and any device that can show the
  project holds its plaintext. Catch-up, new devices and version history all
  require old keys to stay available. What we do instead: key rotation on member
  removal, device revocation and monthly (§6.2), plus compaction so little old
  ciphertext stays on the server (§8.4). Real FS/PCS for key distribution via
  MLS is future work (§13).
- **Mobile** (`Novalist.Mobile`) — desktop first.

## 2. Novalist as it is today

Facts the design has to fit (as of v3.6):

- **Electron + React** front end (`app/`) over a bundled **.NET 8 core process**
  (`Novalist.Backend`), talking **JSON-RPC over stdio**. All project logic lives
  in .NET (`Novalist.Core`).
- **Scene editor:** custom WYSIWYG (contentEditable + DOMPurify sanitizer).
  Codex sections use CodeMirror (Markdown). No Yjs or collaboration code exists.
- **Storage:** a project is a plain folder. Scenes are **HTML** files, entities
  and metadata are **JSON**, images and research files sit alongside. Scene
  identity is a one-line comment in each file; chapter identity is a hidden
  folder marker. Per-project settings live in `.novalist/`.
- **Filesystem is the source of truth:** Novalist reconciles outside changes on
  open and live while running.
- Built-in **Git** client, automatic **ZIP backups**, per-scene **snapshots**.
- **Extensions** are .NET 8 libraries loaded by the core, with UI only in
  sandboxed webviews. They cannot reach into the editor, and the Mac App Store
  build loads none. Hence sync is integrated into Novalist itself, not shipped
  as an extension.
- MIT licensed. The maintainer accepts PRs but promises no maintenance.

## 3. Threat model

| Actor                      | Assumed capability                                                                     | Must not be able to                                           |
| -------------------------- | -------------------------------------------------------------------------------------- | ------------------------------------------------------------- |
| Sync server / its operator | Reads and modifies everything it stores and relays; can drop, reorder, replay messages | Read content, forge edits, silently insert itself as a member |
| Network attacker           | Full control of the network                                                            | Anything beyond what the server can (TLS on top anyway)       |
| Removed member             | Kept all keys they ever had                                                            | Read edits made **after** removal                             |
| Current member (malicious) | Holds the project key                                                                  | Impersonate another member's edits                            |
| Novalist extension         | Runs in the core process (code) or a webview (UI)                                      | — see below                                                   |

Accepted limitations:

- A current member can read everything and can write garbage. E2EE protects
  against outsiders, not against collaborators you invited.
- Without key verification (§8.6), a malicious server can substitute public keys
  when a member is first added.
- The server can withhold updates. Clients detect gaps in per-device sequence
  numbers but cannot force delivery.
- **Extensions run in the same .NET process as the sync engine**, so a malicious
  installed extension could read keys. Keys are never sent to the renderer,
  which at least keeps them away from extension webviews and the web content in
  general. Users must trust the extensions they install, as they already must
  today.

## 4. Architecture

```
┌──────────────── Novalist (fork) ─────────────────────────────┐
│ Electron renderer (React)                                     │
│   Scene editor ── binding: local edits ⇄ remote patches       │
│        │  JSON-RPC over stdio (no keys, no ciphertext)        │
│ Novalist.Backend (.NET 8)                                     │
│   ├─ Novalist.Core (existing project logic, file watcher)     │
│   └─ Novalist.Sync.Client                                     │
│        ├─ YDotNet  (CRDT docs per project item)               │
│        ├─ NSec     (all crypto)                               │
│        ├─ FileBridge (CRDT ⇄ HTML/JSON files on disk)         │
│        ├─ SQLite   (sync state, keys — outside project folder)│
│        └─ SignalR client                                      │
└──────────────────────────┬────────────────────────────────────┘
                           │ TLS WebSocket, ciphertext only
┌──────────────────────────▼────────────────────────────────────┐
│ novalist-sync-server (ASP.NET Core)                           │
│   ├─ SignalR hub (relay, catch-up, presence)                  │
│   ├─ Auth + membership                                        │
│   ├─ PostgreSQL (log, snapshots, wrapped keys, epochs)        │
│   └─ Blob store (encrypted chunks)                            │
└───────────────────────────────────────────────────────────────┘
```

Rule: **if code needs a key or plaintext, it belongs in Novalist.Sync.Client.**
The server never references YDotNet or decrypts anything. The renderer never
sees keys or ciphertext.

### 4.1 Projects and repositories

| Repo                 | Project                                              | Contents                                                                                              |
| -------------------- | ---------------------------------------------------- | ----------------------------------------------------------------------------------------------------- |
| Novalist fork        | `Novalist.Sync.Client` (new)                         | CRDT, crypto, FileBridge, connection, offline queue, snapshots, key management                        |
| Novalist fork        | `Novalist.Backend` / `Novalist.Core` (small changes) | Host Sync.Client, new `sync.*` JSON-RPC methods, hook into file watcher and save path                 |
| Novalist fork        | `app/` (changes)                                     | Editor binding, remote cursors, sharing / members / verification / recovery screens                   |
| novalist-sync-server | `Novalist.Sync.Server`                               | Hub, auth, persistence, blob endpoints, snapshot retention                                            |
| novalist-sync-server | `Novalist.Sync.Contracts`                            | Wire records, hub interface, protocol version. Published as a **NuGet package** consumed by the fork. |

Changes to Novalist itself are kept as small and isolated as possible so they
can be offered upstream as a PR. Talk to the maintainer before the PR gets
large.

### 4.2 Licensing

| Project                   | License           |
| ------------------------- | ----------------- |
| `Novalist.Sync.Client`    | MIT               |
| `Novalist.Sync.Contracts` | MIT               |
| `Novalist.Sync.Server`    | AGPL-3.0-or-later |

Sync.Client lives in the Novalist fork and shares Novalist's MIT license.
Contracts is MIT so the fork and third-party clients can depend on it without
taking on copyleft. The server is AGPL so anyone hosting a modified server for
others must publish their changes (AGPL §13).

## 5. Data model

### 5.1 The project is the sync unit

One shared project = one **sync space** with one member list and one key
hierarchy. Inside it, every item is its own CRDT document (YDotNet `Doc`),
identified by a random 128-bit `itemId`:

| Item kind                                                                  | CRDT shape                                                                                                                                                                                           | On disk                                 |
| -------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------- |
| **Structure** (one per project)                                            | Y.Map/Y.Array: books, drafts, acts, chapters, scene order, metadata                                                                                                                                  | chapter folders + markers, project JSON |
| **Scene**                                                                  | Y.XmlFragment mirroring the editor's sanitized block structure (paragraph styles, lists, footnotes, comments; inline formatting as attributes) + Y.Map for synopsis, notes, POV, status, dates, tags | scene `.html`                           |
| **Codex entry / other JSON** (entities, plotlines, timeline, boards, maps) | Y.Map per file; long-form Markdown sections as Y.Text                                                                                                                                                | `.json` / `.md`                         |
| **File manifest** (one per project)                                        | Y.Map: relative path → `BlobRef` (§9.2)                                                                                                                                                              | images, research, any other file        |

Scenes are only loaded into memory when opened or when updates for them arrive.

The `itemId ↔ Novalist identity` mapping (scene ID comment, chapter marker, file
path) lives inside the encrypted structure doc, so the server never sees names
or paths.

### 5.2 FileBridge: CRDT ⇄ filesystem

Novalist treats the folder as the source of truth, so both directions are
needed:

- **CRDT → disk:** after local or remote changes, the bridge writes the affected
  file (debounced, ~500 ms), using Novalist's existing serializers so files look
  exactly as they do today. It records the hash of what it wrote.
- **Disk → CRDT:** when the existing file watcher reports a change whose hash is
  _not_ one the bridge just wrote (an outside edit, a `git pull`, a restored
  backup), the bridge diffs old vs. new content and applies the difference as
  CRDT edits: text diff for scenes and Markdown, field diff for JSON.
  Deleted/added files become structure or manifest changes.
- **Echo suppression** by content hash prevents write → watcher → re-apply
  loops.

### 5.3 Editor binding

The renderer keeps working on HTML. The binding is a thin protocol over
JSON-RPC:

- **Local edits:** the editor sends small change notifications (changed
  block(s) + selection) as you type, batched every ~100 ms. The backend diffs
  them against the scene's Y.XmlFragment and applies minimal CRDT operations.
- **Remote edits:** the backend sends block-level patches (insert / replace /
  delete block N) to the renderer, which applies them without re-rendering the
  whole scene.
- **Cursors** are exchanged as Yjs _relative positions_, so the local caret and
  remote cursors stay attached to the right character while text around them
  changes.

This binding is the largest piece of new work, because Novalist's editor has no
existing CRDT integration.

## 6. Cryptography

### 6.1 Primitives

| Purpose                     | Algorithm                                 | Library            |
| --------------------------- | ----------------------------------------- | ------------------ |
| Symmetric encryption (AEAD) | XChaCha20-Poly1305 (24-byte random nonce) | NSec               |
| Signatures                  | Ed25519                                   | NSec               |
| Key agreement               | X25519                                    | NSec               |
| Key derivation              | HKDF-SHA256                               | NSec / .NET `HKDF` |
| Hashing                     | SHA-256                                   | .NET               |
| Randomness                  | `RandomNumberGenerator`                   | .NET               |

XChaCha20-Poly1305's 192-bit nonce is safe to generate randomly, so nonce reuse
is not a concern even with many devices writing under the same key. All
derivations use versioned `info` strings (`"novalist/v1/..."`) for domain
separation.

### 6.2 Keys

**Device identity keys.** Each Novalist installation generates an Ed25519 key
pair (`DSK`, signing) and an X25519 key pair (`DXK`, receiving keys). Public
halves are uploaded; private halves stay on the device in a SQLite database **in
the app-data directory, never inside the project folder** (so Git, backups and
folder sync never pick them up), encrypted at rest with DPAPI / macOS Keychain /
libsecret.

New devices of the same user are **linked** by an existing device (QR or pairing
code), which signs the new device's public keys.

**Project epoch keys.** Each project has a sequence of epochs, each with a
random 32-byte key `EK_e`. Subkeys per purpose:

```
K_update   = HKDF(EK_e, salt = projectId, info = "novalist/v1/update")
K_snapshot = HKDF(EK_e, salt = projectId, info = "novalist/v1/snapshot")
K_presence = HKDF(EK_e, salt = projectId, info = "novalist/v1/presence")
```

**A new epoch starts when:**

- a member is removed,
- a device is revoked (lost/stolen laptop),
- **30 days** have passed since the current epoch started.

Rotation is a compare-and-set on the server: the first client to submit epoch
`e+1` wins; a client whose rotation is rejected adopts the winner's epoch. Old
epoch keys are kept, so history stays readable.

**Blob keys.** Each blob has its own random 32-byte key, stored inside the
encrypted file manifest (§9.2).

### 6.3 Key wrapping

To give `EK_e` to recipient device `R`, a granting member device `G`:

```
eph       = new X25519 key pair (single use)
shared    = X25519(eph.private, R.DXK.public)
wrapKey   = HKDF(shared, salt = eph.public || R.DXK.public,
                 info = "novalist/v1/wrap" || projectId || epoch)
nonce     = random(24)
aad       = projectId || epoch || R.deviceId || G.deviceId
wrapped   = XChaCha20Poly1305.Encrypt(wrapKey, nonce, EK_e, aad)
signature = Ed25519.Sign(G.DSK, "novalist/v1/wrap-sig" || aad || eph.public || nonce || wrapped)
```

**The signature is essential.** Without it the server could wrap a key _it_
knows, the recipient would encrypt edits under it, and the server could read
them. The recipient verifies the signature and accepts the key only if `G` is a
device it already trusts as a member of this project.

### 6.4 Recovery key

At account setup the user is shown a **recovery key** once (256 random bits,
displayed as a word list or grouped base32). From it the client derives a
**recovery device**:

```
recSeed = HKDF(recoveryKey, info = "novalist/v1/recovery")
recovery device = Ed25519 + X25519 key pairs derived from recSeed
```

The recovery device is registered like a normal device, so every project's epoch
keys are automatically wrapped for it — no separate backup to keep in sync.

To recover: on a new installation the user enters the recovery key, the client
re-derives the recovery device, unwraps the epoch keys, and uses the recovery
device's signing key to link the new real device. Lose all devices **and** the
recovery key → data is gone. Anyone holding the recovery key has full access, so
the UI must say so clearly.

## 7. Message format

```
header = protoVersion(1) || msgType(1) || projectId(16) || itemId(16)
         || epoch(4) || senderDeviceId(16) || senderSeq(8)
```

The header is 62 bytes. `msgType` ∈ { update = 1, snapshot = 2, presence = 3 };
0 and all other values are invalid. `itemId` is a random GUID, so the server
learns _that_ an item exists and how often it changes, but not what it is.

Encoding rules:

- Integers (`epoch` as uint32, `senderSeq` as uint64) are **big-endian**.
- IDs (`projectId`, `itemId`, `senderDeviceId`) are **random UUIDv4**, written in
  RFC 9562 (big-endian) byte order. Not UUIDv7: its embedded timestamp would
  tell the server when each item, project or device was created.
- A receiver checks `protoVersion` **before** anything else, including the
  length, and rejects unsupported versions instead of parsing them with the
  current layout.

```
nonce      = random(24)
ciphertext = XChaCha20Poly1305.Encrypt(K_type, nonce, payload, aad = header)
signature  = Ed25519.Sign(DSK, "novalist/v1/msg-sig" || header || nonce || ciphertext)
envelope   = { header, nonce, ciphertext, signature }
```

## 8. Protocol flows

### 8.1 Sharing an existing local project

1. Client generates `projectId`, `EK_0`, and `itemId`s for every scene, Codex
   file and asset.
2. Builds CRDT docs from the existing files via the FileBridge (§5.2); uploads
   assets as blobs (§9).
3. Wraps `EK_0` for the user's own devices and recovery device; uploads wrapped
   keys and an initial snapshot of every item.

### 8.2 Sending an edit

The editor binding or FileBridge produces a YDotNet update for one item. The
client seals it (§7) with `K_update`, increments its `senderSeq`, and pushes it.
The server checks membership, assigns `serverSeq`, stores, broadcasts.

### 8.3 Receiving an edit

1. Look up the sender device; verify it is a member.
2. Verify the signature; **reject before decrypting** if invalid.
3. Check the epoch key is available.
4. Decrypt; reject on failure.
5. Apply to the item's YDotNet doc inside try/catch (a malformed update must not
   crash Novalist).
6. FileBridge writes the file; the renderer gets a patch if the scene is open.

### 8.4 Catch-up, snapshots and retention

- On connect: `CatchUp(projectId, lastServerSeq)`, process as in §8.3.
- A client creates a snapshot of an item **every 500 updates to that item, or
  when the project is closed**. It is sealed with `K_snapshot` and uploaded with
  `coversServerSeq = N`.
- Updates to that item with `serverSeq <= N` that are covered by a retained
  snapshot are deleted.
- **Tiered retention** (enforced by the server, which only needs timestamps):
  keep snapshots **hourly for 24 hours, daily for 30 days, weekly beyond that**.
  These form the shared version history; Novalist's local per-scene snapshots
  continue to work as before.

### 8.5 Adding a member

1. Inviter fetches the invitee's device list (with linking signatures) and
   verifies it.
2. Inviter shows the safety number (§8.6).
3. Inviter wraps **all epoch keys** for each invitee device and recovery device
   — new members see full history by default.
4. Server records membership.

### 8.6 Key verification

```
safetyNumber = SHA-256( sort(userA.identityKeys) || sort(userB.identityKeys) )
```

Shown as digits or a QR code for comparison over another channel. Clients
remember verified keys and **warn loudly** when a known user's keys change.

### 8.7 Removing a member / revoking a device

1. Create `EK_{e+1}` (compare-and-set, §6.2).
2. Wrap it only for remaining devices.
3. Upload fresh snapshots of all items under the new epoch.
4. Server drops the membership/device and stops serving updates and blobs to it.

### 8.8 Presence

Cursors, selections and "is in scene X" are sealed with `K_presence`, broadcast,
never stored.

## 9. Blobs (images, research files, other assets)

### 9.1 Encryption

```
BK     = random(32);  blobId = random(16);  chunkSize = 64 KiB
chunk i: nonce = 16 zero bytes || uint64_be(i)        // unique because BK is single-use
         aad   = "novalist/v1/blob" || blobId || uint64_be(i) || isFinal(1)
         c_i   = XChaCha20Poly1305.Encrypt(BK, nonce, chunk_i, aad)
```

`isFinal` prevents truncation; the index prevents reordering. Chunks transfer
independently, so large files stream and resume.

### 9.2 References

The file manifest (§5.1) maps each relative path to:

```json
{
    "blobId": "...",
    "key": "<BK base64>",
    "size": 1234567,
    "chunkSize": 65536,
    "mime": "image/png",
    "sha256": "<plaintext hash>"
}
```

Changing a file creates a new blob and updates the manifest entry. Paths, names,
MIME types and keys are only visible inside the encrypted manifest.

### 9.3 Rules

- No convergent encryption (no content-derived keys): it would reveal when two
  users hold the same file.
- Garbage collection: clients periodically report referenced blob IDs; the
  server deletes unreferenced blobs after a grace period.
- On member removal, existing blobs stay under their old keys (re-encryption:
  v2).

## 10. What the server stores and sees

| Table          | Contents                                                       | Server can read? |
| -------------- | -------------------------------------------------------------- | ---------------- |
| `users`        | account, credentials                                           | yes              |
| `devices`      | deviceId, userId, public keys, linking signature, `isRecovery` | public keys only |
| `projects`     | projectId, current epoch, epoch start time                     | yes              |
| `memberships`  | projectId, userId, role                                        | yes              |
| `wrapped_keys` | §6.3 records                                                   | no               |
| `updates`      | serverSeq, header, nonce, ciphertext, signature                | header only      |
| `snapshots`    | coversServerSeq, timestamp, envelope                           | header only      |
| `blob_chunks`  | blobId, index, ciphertext                                      | no               |

Visible metadata: project and item counts, member lists, devices, timestamps,
edit frequency per item, sizes.

## 11. Decision log

| #   | Decision                | Choice                                                                                         |
| --- | ----------------------- | ---------------------------------------------------------------------------------------------- |
| D1  | Repository layout       | Two repos: Novalist fork (incl. Sync.Client) + novalist-sync-server (incl. Contracts as NuGet) |
| D2  | Integration             | Fork Novalist, keep changes small, offer upstream as PR                                        |
| D3  | Where CRDT + crypto run | All in the .NET backend (YDotNet, NSec, SignalR); renderer gets no keys                        |
| D4  | Sync scope              | Whole project: scenes live, Codex/metadata as CRDT maps, files as encrypted blobs              |
| D5  | History for new members | Full history by default (all epoch keys wrapped)                                               |
| D6  | Recovery                | Recovery key in v1, implemented as a derived recovery device                                   |
| D7  | Key rotation            | On member removal, device revocation, and every 30 days                                        |
| D8  | Version history         | Tiered snapshots: hourly 24 h, daily 30 d, weekly after                                        |
| D9  | Snapshot trigger        | Every 500 updates per item, or on project close                                                |
| D10 | Forward secrecy         | Not in v1 (rationale §1); rotation + compaction instead                                        |

## 12. Open questions

- [ ] **Git coexistence:** a `git pull` on a synced project arrives as outside
      file changes and is merged into the CRDT. Is that wanted, or should Git be
      discouraged / disabled for synced projects?
- [ ] Should the server verify Ed25519 signatures (public keys only) to drop
      spam from non-member devices early?
- [ ] Exact Y.XmlFragment schema for the scene editor's block and inline formats
      (needs a read of the editor and sanitizer code).
- [ ] Mobile (`Novalist.Mobile`) support later?

## 13. Future work

- MLS (RFC 9420) for forward secrecy and post-compromise security of key
  distribution.
- Ciphertext padding to reduce size leakage.
- Key transparency to detect key substitution without manual verification.
- Re-encrypting blobs on member removal.

## 14. Build order

1. **Server skeleton + Contracts:** hub, auth, PostgreSQL log, catch-up.
   Plaintext for now.
2. **Sync.Client with YDotNet, plaintext:** two Novalist instances syncing one
   scene's text through the server. Test: concurrent edits converge.
3. **FileBridge:** CRDT ⇄ scene HTML, echo suppression, outside edits.
4. **Editor binding:** live typing, remote patches, cursors.
5. **Encryption:** single epoch key, sealed envelopes.
6. **Identity keys, wrapping, signatures, membership, safety numbers.**
7. **Whole project:** structure doc, Codex maps, file manifest + blobs.
8. **Snapshots, compaction, tiered retention.**
9. **Epochs:** removal, revocation, monthly rotation.
10. **Recovery key, device linking.**
