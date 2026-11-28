# Provisioning ledger operations runbook

Owner: Platform Operations with the Security Architect. Status: **Proposed** (DL-108). Pending independent review and a person's approval.

This runbook covers the connector operation ledger (`[provisioning].[OperationLedger]` on SQL Server, `provisioning.operation_ledger` on PostgreSQL). Each connector records there the outcome of every operation it finished, so that a repeat of the same operation id changes nothing twice. The table is append-only: a trigger refuses every update and delete (`TR_OperationLedger_AppendOnly`; on PostgreSQL `tr_operation_ledger_append_only` and `tr_operation_ledger_no_truncate`, both `ENABLE ALWAYS`).

> **Not tested.** The scripts below have been reviewed but are not run by any automated test. The local gate runs SQL Server only, and nothing exercises this procedure. Rehearse it on a copy of the database before using it in production.

## Production prerequisite: database audit of the ledger (DL-108)

The append-only trigger does not stop the schema owner. The migrator login can disable the trigger and delete rows whenever it likes. If a ledger row is gone, the same operation id can be sent to the target again. The IAM audit trail does not see any of this. So before a production go-live, the database itself must record and alert on it:

- **SQL Server:** a server audit with a database audit specification covering `SCHEMA_OBJECT_CHANGE_GROUP` (this records `DISABLE TRIGGER` and `ENABLE TRIGGER`) and `DELETE`, `UPDATE` on `[provisioning].[OperationLedger]` by `public`. The audit writes to a target the database administrators can't clear.
- **PostgreSQL:** `pgaudit` with `pgaudit.log = 'ddl, write'` for the migrator role, or object auditing on `provisioning.operation_ledger`. The log is shipped to a store the database administrators can't clear.
- **Alert:** any such event not covered by an approved change record raises a security alert.

This is a deployment control. It is not built or checked in this repository (R-040).

## What `LedgerDamaged` means (DL-107, DL-108)

An item that fails with the problem `LedgerDamaged` has a step with the same code, and an error log names the item. It means a connector's ledger handed the SDK a record the SDK never writes, so nobody can tell whether that step changed the target. Records the SDK never writes are:

- a failure;
- the SDK's own `RateLimited` code;
- a status it doesn't know;
- a record for another operation;
- a `TargetNotFound` or `RelatedNotFound` code;
- a fingerprint that isn't 64 lower-case hex digits.

The step is never retried.

- **Earlier steps are undone first,** as for a rejected step. On an account this item created, the undo removes everything on it, the damaged step's change included. On an existing account, the damaged step's own change, if it was made, is left for the person.
- **A damaged create** has no undo. An account the create may have made on the target is left as it is (in AD, possibly enabled). Reconcile it by hand.
- **A damaged undo step** leaves the item `UndoFailed`.

Only the SDK's own refusal is reported this way. A connector that reports the code itself, or that is not on the SDK and throws the SDK's exception, has an ordinary connector fault.

Find out how the record got there (treat it as possible tampering, and check the database audit above), read the target, and reconcile the access by hand. The person's work queue must cover `LedgerDamaged` items as well as `OutcomeUnknown`.

## Blocked upgrade: removing a stored `Failed` row (`LedgerNoFailed`)

Migration `LedgerNoFailed` (DL-106) stops the upgrade if the ledger holds a `Failed` row. On SQL Server this is error `51002`; on PostgreSQL it is error `P0001`, with a message naming DL-106. Nothing is deleted. The SDK never records failures, so such a row should not exist: treat it as possible tampering.

### Once per database (before any change)

The database administrator creates an evidence schema. It is owned by the administrator, and the provisioning, migrator and application logins get no rights to it.

```sql
-- SQL Server
CREATE SCHEMA [ledger_evidence] AUTHORIZATION [dbo];
CREATE TABLE [ledger_evidence].[OperationLedgerRemoved] (
    [OperationId] uniqueidentifier NOT NULL, [Fingerprint] nchar(64) NOT NULL, [Status] nvarchar(32) NOT NULL,
    [Code] nvarchar(64) NULL, [RecordedAtUtc] datetimeoffset NOT NULL,
    [ChangeRecord] nvarchar(64) NOT NULL, [RemovedAtUtc] datetimeoffset NOT NULL);
GRANT INSERT ON [ledger_evidence].[OperationLedgerRemoved] TO <migrator login user>;  -- insert only: no select, update or delete
```

```sql
-- PostgreSQL
CREATE SCHEMA ledger_evidence;
CREATE TABLE ledger_evidence.operation_ledger_removed (
    operation_id uuid NOT NULL, fingerprint char(64) NOT NULL, status varchar(32) NOT NULL, code varchar(64) NULL,
    recorded_at_utc timestamptz NOT NULL, change_record varchar(64) NOT NULL, removed_at_utc timestamptz NOT NULL);
GRANT USAGE ON SCHEMA ledger_evidence TO <migrator role>;
GRANT INSERT ON ledger_evidence.operation_ledger_removed TO <migrator role>;
```

### Procedure

1. **Stop and approve.** Stop every provisioning worker. Open a change record. A second person, not the operator, approves it before step 4.
2. **Export.** Export the failed rows and attach them to the change record, together with a SHA-256 hash of the export file:
   - SQL Server: `SELECT * FROM [provisioning].[OperationLedger] WHERE [Status] = N'Failed' ORDER BY [OperationId];`
   - PostgreSQL: `SELECT * FROM provisioning.operation_ledger WHERE status = 'Failed' ORDER BY operation_id;`
3. **Find the affected items.** Attach the result to the change record. Every item listed here has an unknown outcome for that step.
   - SQL Server: `SELECT o.[ItemId], o.[Step], o.[Action], i.[State], i.[Problem] FROM [provisioning].[ProvisioningOperation] o JOIN [provisioning].[ProvisioningItem] i ON i.[Id] = o.[ItemId] WHERE o.[Id] IN (<exported operation ids>);`
   - PostgreSQL: `SELECT o.item_id, o.step, o.action, i.state, i.problem FROM provisioning.provisioning_operation o JOIN provisioning.provisioning_item i ON i.id = o.item_id WHERE o.id IN (<exported operation ids>);`
4. **Move the rows.** As the migrator login, run the script for your engine. Use exactly the exported ids. It moves only `Failed` rows, and it rolls everything back unless both the evidence copy and the delete touch exactly as many rows as were exported.

   SQL Server:

   ```sql
   SET XACT_ABORT ON;
   BEGIN TRANSACTION;
   DECLARE @ids TABLE ([OperationId] uniqueidentifier PRIMARY KEY);
   INSERT INTO @ids ([OperationId]) VALUES ('<id 1>'), ('<id 2>');
   DECLARE @expected int = (SELECT COUNT(*) FROM @ids);

   INSERT INTO [ledger_evidence].[OperationLedgerRemoved] ([OperationId], [Fingerprint], [Status], [Code], [RecordedAtUtc], [ChangeRecord], [RemovedAtUtc])
   SELECT l.[OperationId], l.[Fingerprint], l.[Status], l.[Code], l.[RecordedAtUtc], N'<change record id>', SYSDATETIMEOFFSET()
   FROM [provisioning].[OperationLedger] l JOIN @ids i ON i.[OperationId] = l.[OperationId] WHERE l.[Status] = N'Failed';
   IF @@ROWCOUNT <> @expected THROW 51900, N'Evidence rows differ from the export; nothing removed.', 1;

   DISABLE TRIGGER [provisioning].[TR_OperationLedger_AppendOnly] ON [provisioning].[OperationLedger];
   DELETE l FROM [provisioning].[OperationLedger] l JOIN @ids i ON i.[OperationId] = l.[OperationId] WHERE l.[Status] = N'Failed';
   IF @@ROWCOUNT <> @expected THROW 51901, N'Deleted rows differ from the export; rolled back.', 1;
   ENABLE TRIGGER [provisioning].[TR_OperationLedger_AppendOnly] ON [provisioning].[OperationLedger];
   COMMIT TRANSACTION;
   ```

   PostgreSQL (in `psql` with `\set ON_ERROR_STOP on`):

   ```sql
   BEGIN;
   CREATE TEMP TABLE removed_ids (operation_id uuid PRIMARY KEY) ON COMMIT DROP;
   INSERT INTO removed_ids VALUES ('<id 1>'), ('<id 2>');
   DO $$
   DECLARE expected int; moved int; deleted int;
   BEGIN
     SELECT count(*) INTO expected FROM removed_ids;
     INSERT INTO ledger_evidence.operation_ledger_removed (operation_id, fingerprint, status, code, recorded_at_utc, change_record, removed_at_utc)
     SELECT l.operation_id, l.fingerprint, l.status, l.code, l.recorded_at_utc, '<change record id>', now()
     FROM provisioning.operation_ledger l JOIN removed_ids i USING (operation_id) WHERE l.status = 'Failed';
     GET DIAGNOSTICS moved = ROW_COUNT;
     IF moved <> expected THEN RAISE EXCEPTION 'Evidence rows differ from the export; nothing removed.'; END IF;
     ALTER TABLE provisioning.operation_ledger DISABLE TRIGGER tr_operation_ledger_append_only;
     DELETE FROM provisioning.operation_ledger l USING removed_ids i WHERE i.operation_id = l.operation_id AND l.status = 'Failed';
     GET DIAGNOSTICS deleted = ROW_COUNT;
     IF deleted <> expected THEN RAISE EXCEPTION 'Deleted rows differ from the export; rolled back.'; END IF;
     ALTER TABLE provisioning.operation_ledger ENABLE ALWAYS TRIGGER tr_operation_ledger_append_only;
   END $$;
   COMMIT;
   ```

   On both engines, the trigger change is part of the transaction: a rollback leaves the trigger on.
5. **Check.** The second person checks:
   - the trigger is on again: `sys.triggers.is_disabled = 0` for `TR_OperationLedger_AppendOnly`; on PostgreSQL, `pg_trigger.tgenabled = 'A'` for both ledger triggers;
   - no `Failed` row is left in the ledger;
   - the evidence table holds exactly the exported rows;
   - the database audit recorded the trigger change and the delete under this change record.
6. **Reconcile before restarting.** Each affected item from step 3 has an unknown outcome. An item that is not `Failed` or `Succeeded` would run its step again under the same operation id, and with the ledger row gone the connector would be called again. There is no per-item hold yet (R-040), so the workers stay stopped until a person has read the target for every affected item, reconciled it by hand, and recorded it in the change record. Then run the upgrade again and restart the workers.

This procedure is manual and outside the IAM audit trail. Its records are the change record and the database audit.
