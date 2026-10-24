# Collaboration migrations

Each stacked change adds only the schema it uses: verified device keys,
invitations, epoch rotation, then field leases. Relay, membership management and
presence need no new tables. Migrations extend the original `InitialCreate`
schema; regression tests preserve populated users, devices, memberships and
updates through upgrade, rollback and reapplication.

Back up persistent databases before upgrading. Development databases that ran
the earlier combined `CollaborationAccess` migration or intermediate unreleased
migrations need recreation if disposable, or a separate data-preserving
reconciliation. Do not apply the stack over that unrelated migration history.
Relay deployments need no database or migrations.
