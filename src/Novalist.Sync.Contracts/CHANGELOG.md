# Changelog

All notable changes to `Novalist.Sync.Contracts` are documented here. The
format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
the package uses [Semantic Versioning](https://semver.org/): breaking wire
changes bump `Protocol.Version` and the major version, additive changes bump
the minor version.

## [Unreleased]

### Added

- `Protocol.Version` (protocol version 1).
- `MsgType` enum: `Update = 1`, `Snapshot = 2`, `Presence = 3`.
- `EnvelopeHeader` record with the 62-byte layout from design.md §7:
  `WriteTo`, `ToBytes` and `Parse` (big-endian integers, RFC 9562 UUID byte
  order).
- `UnsupportedProtocolVersionException`, thrown by `EnvelopeHeader.Parse` for
  an unknown `protoVersion`.
