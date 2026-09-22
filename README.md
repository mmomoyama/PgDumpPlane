# PgDumpPlane

`PgDumpPlane` is a .NET library that writes a PostgreSQL database as a
`pg_dump`-style plain-text SQL script. It uses Npgsql only; it does not start
the native `pg_dump` executable.

The implementation follows the important ordering and consistency rules in
PostgreSQL's `src/bin/pg_dump`: catalog and data reads share a read-only
snapshot, table data is streamed with an explicit-column `COPY`, sequence
state is emitted separately with `setval`, and indexes and constraints are
written after table data.

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

Restore with a current `psql` client:

```shell
psql --set ON_ERROR_STOP=on --dbname target --file database.sql
```

Set `UsePsqlRestrict = false` when the output must be consumed as SQL by a
client other than `psql`. The default emits the `\restrict`/`\unrestrict`
guard used by current PostgreSQL releases.

## Current scope

The package supports PostgreSQL 13 or later and writes:

- user schemas and extensions;
- enum types and routines;
- ordinary, unlogged, and partitioned tables;
- sequences, ownership, and current sequence state;
- table rows in PostgreSQL text `COPY` format;
- primary, unique, check, exclusion, and foreign-key constraints;
- standalone indexes, views, and triggers.

This is not yet a byte-for-byte or feature-complete replacement for native
`pg_dump`. Version 0.1 does not dump ownership/ACLs, comments, domains,
standalone composite types, foreign tables, materialized views, large objects,
row-security policies, publications/subscriptions, statistics objects, or
security labels. For those objects, or for cross-major-version migrations,
use the native `pg_dump` tool.

## Build a NuGet package

```shell
dotnet test PgDumpPlane.slnx
dotnet pack src/PgDumpPlane/PgDumpPlane.csproj -c Release -o artifacts
```

## PostgreSQL attribution

The design was informed by the PostgreSQL `pg_dump` sources, particularly
`pg_dump.c` and `pg_backup_archiver.c`. PostgreSQL is distributed under the
[PostgreSQL License](https://www.postgresql.org/about/licence/). This project
does not redistribute PostgreSQL source code.
