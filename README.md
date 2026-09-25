# PgDumpPlane

`PgDumpPlane` is a .NET library that writes and restores PostgreSQL databases
as `pg_dump`-style plain-text SQL scripts. It uses Npgsql only; it does not
start the native `pg_dump` or `psql` executable.

The implementation follows the important ordering and consistency rules in
PostgreSQL's `src/bin/pg_dump`: catalog and data reads share a read-only
snapshot, table data is streamed with an explicit-column `COPY` or `INSERT`,
sequence state is emitted separately with `setval`, and indexes and constraints
are written after table data.

## Install

```shell
dotnet add package PgDumpPlane
```

## Use

```csharp
using PgDumpPlane;

await using var output = File.Create("database.sql");

var options = new PgDumpOptions();
options.ExcludeSchemas.Add("audit");

await new PostgresPlainTextDumper().DumpAsync(
    "Host=localhost;Database=app;Username=postgres;Password=secret",
    output,
    options);
```

You can also pass an `NpgsqlDataSource`, or an open `NpgsqlConnection` and a
`TextWriter`. Output is UTF-8 without a byte-order mark and uses LF newlines.
Rows are copied directly from Npgsql's text COPY reader to the destination, so
large tables are not buffered in memory.

To generate explicit-column `INSERT` statements instead of COPY blocks:

```csharp
var options = new PgDumpOptions
{
    DataFormat = PgDumpDataFormat.Inserts
};
```

INSERT rows are also streamed without buffering the whole table. COPY remains
the default because it is smaller and generally restores faster.

## Restore

Check a file and restore it through Npgsql with the library:

```csharp
var restorer = new PostgresPlainTextRestorer();

if (!await restorer.IsValidDumpFileAsync("database.sql"))
    throw new InvalidDataException("Not a PgDumpPlane dump file.");

await restorer.RestoreFileAsync(
    "Host=localhost;Database=app;Username=postgres;Password=secret",
    "database.sql");
```

Both explicit-column `INSERT` statements and PostgreSQL text
`COPY ... FROM stdin` blocks are supported. SQL statements and COPY rows are
streamed instead of buffering the entire dump in memory. The restorer also
understands the `\restrict`/`\unrestrict` guards emitted by this package.
Every restore entry point validates the header before starting a transaction or
executing SQL. Plain-text files produced by PgDumpPlane and native `pg_dump`
are accepted. Arbitrary SQL files and the custom, directory, or tar archive
formats produced by `pg_dump` are rejected; use `pg_restore` for those archive
formats.

By default, restore runs in one transaction so a failure rolls back the whole
operation. This can be changed when a transaction is not appropriate:

```csharp
var options = new PgRestoreOptions
{
    UseTransaction = false,
    CommandTimeout = 0
};
```

Restore into an empty database, or into a database where the dumped objects do
not already exist. A plain-text dump is executable SQL; only restore files from
a trusted source.

You can alternatively restore with a current `psql` client:

```shell
psql --set ON_ERROR_STOP=on --dbname target --file database.sql
```

The default dump emits the `\restrict`/`\unrestrict` guard used by current
PostgreSQL releases. Both this package's restorer and current `psql` clients
understand the guard. Set `UsePsqlRestrict = false` for other SQL clients.

## Current scope

The package supports PostgreSQL 12 through 18 and writes:

- user schemas and extensions;
- enum types and routines;
- ordinary, unlogged, and partitioned tables;
- sequences, sequence-to-column ownership, and current sequence state;
- table rows in PostgreSQL text `COPY` format or explicit-column `INSERT` statements;
- primary, unique, check, exclusion, and foreign-key constraints;
- standalone indexes, views, and triggers;
- ownership and explicit object or column privileges for schemas, enum types,
  routines, tables, views, and sequences.

Extension statements omit an explicit `VERSION`, so the destination server
installs its default available version. For example, pgcrypto is written as
`CREATE EXTENSION IF NOT EXISTS "pgcrypto" WITH SCHEMA "public";`.

Roles are cluster-wide objects and are not created by a database dump. Every
owner and grantee referenced by a dump must already exist on the destination
server. Set `IncludeOwnership` or `IncludePrivileges` to `false` when those
settings should not be restored. PostgreSQL does not provide an `ALTER
EXTENSION ... OWNER TO` command, so extensions are owned by the user that runs
the restore.

Ownership is enabled by default. It is restored with statements such as
`ALTER SCHEMA "app" OWNER TO "postgres";` and `ALTER FUNCTION
"app"."calculate"() OWNER TO "postgres";`.

As with `pg_dump` without `--create`, database-level ownership and privileges
are not written. A database created by the WinForms sample is owned by the
connection user.

Version-specific catalog and SQL differences are selected from the connected
server's major version:

| PostgreSQL | Version-specific handling |
| --- | --- |
| 12–13 | Stored generated columns and the common PostgreSQL 12 catalog baseline |
| 14 | Per-column `pglz`/`lz4` compression |
| 15 | Unlogged sequences and `NULLS NOT DISTINCT` constraints |
| 16 | PostgreSQL 16 catalog-compatible output |
| 17 | `transaction_timeout` session and restore settings |
| 18 | Virtual generated columns and named/`NO INHERIT` NOT NULL constraints |

Constraint syntax introduced by newer releases is retained through
`pg_get_constraintdef()`. Servers older than 12 and newer than 18 are rejected
instead of risking an invalid dump. CI runs the integration test against every
PostgreSQL major version from 12 through 18.

This is not yet a byte-for-byte or feature-complete replacement for native
`pg_dump`. Version 0.2 does not dump comments, domains, standalone composite
types, foreign tables, materialized views, large objects,
row-security policies, publications/subscriptions, statistics objects, or
security labels. For those objects, or for cross-major-version migrations,
use the native `pg_dump` tool.

## Build a NuGet package

```shell
dotnet test PgDumpPlane.slnx
dotnet pack src/PgDumpPlane/PgDumpPlane.csproj -c Release -o artifacts
```

## WinForms sample

`samples/PgDumpPlane.WinForms` contains a Windows desktop application for
creating and restoring dump files. Enter the PostgreSQL host, credentials,
database, and file path, then select the dump or restore operation.

The dump tab exposes schema filters, schema/data selection, COPY or INSERT
format, unlogged-table data, ownership, privileges, snapshot mode, and the
`psql` restrict guard. The restore tab exposes transaction handling and command
timeout settings.

The restore operation validates the dump first, disconnects users from the
target database, drops and recreates that database, and then restores the dump.
It permanently removes the target database's existing contents and therefore
shows a confirmation dialog before proceeding. The PostgreSQL user must have
permission to terminate connections and create or drop the target database.

Run the sample on Windows with:

```shell
dotnet run --project samples/PgDumpPlane.WinForms
```

## PostgreSQL attribution

The design was informed by the PostgreSQL `pg_dump` sources, particularly
`pg_dump.c` and `pg_backup_archiver.c`. PostgreSQL is distributed under the
[PostgreSQL License](https://www.postgresql.org/about/licence/). This project
does not redistribute PostgreSQL source code.
