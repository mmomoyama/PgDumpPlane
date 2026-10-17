# PostgreSQL 19 RC 1 source review

Reviewed on 2026-10-17 against the official PostgreSQL 19 Beta 4 and RC 1
source archives, not the moving development branch:

- [PostgreSQL 19 RC 1 announcement](https://www.postgresql.org/about/news/postgresql-19-rc-1-released-3409/)
- [RC 1 source archive](https://ftp.postgresql.org/pub/source/v19rc1/postgresql-19rc1.tar.bz2)
- [Beta 4 source archive](https://ftp.postgresql.org/pub/source/v19beta4/postgresql-19beta4.tar.bz2)

The RC 1 archive SHA-256 was checked against the publisher's checksum:
`af105fc8e960fc836a09fc2cd7f582b49fd17675d9d36631abe3a565bfd7d701`.

## Findings and implementation

- `src/bin/pg_dump/pg_dump.c` adds `appendIndexStatTargets()` and calls it
  from both `dumpIndex()` and `dumpConstraint()`. The RC 1 regression test
  covers an exclusion constraint with expression-index statistics targets.
  PgDumpPlane now reads explicit index-column `attstattarget` values and emits
  `ALTER INDEX ... ALTER COLUMN <position> SET STATISTICS <target>` after the
  index or backing constraint is created. Zero is retained; default targets
  (`-1` or `NULL`) are omitted. The catalogs and syntax are also available on
  the older supported servers, so this preservation is not restricted to 19.
- The relevant catalog header layouts and `pg_dump_sort.c` do not change
  between these archives. No RC-specific catalog-layout or object-order switch
  is required for the object kinds currently supported by this library.
- `pg_backup_archiver.c` guards reconnect-time restrict commands when a key
  is absent. PgDumpPlane does not emit database-reconnect commands; its existing
  optional restrict guard requires no corresponding change.
- Compressed archive readers receive error-handling fixes. PgDumpPlane supports
  plain-text SQL, not the custom/directory archive formats, so those fixes do
  not introduce another supported input format.
- RC version text (`19rc1`) already uses the leading-major-number detection
  path. Added coverage for both native and PgDumpPlane headers and downgrade
  restoration. Updated the CI service image from `postgres:19beta4` to
  `postgres:19rc1`.

## Local verification

- All 61 tests passed against PostgreSQL 19 RC 1, including comparisons with
  its native `pg_dump` for definition/owner order and index statistics SQL,
  plus dump/restore round trips. A temporary Windows-to-container bridge ran
  the real RC 1 executable; no SQL output was substituted.
- All 61 tests passed against PostgreSQL 18, including native-tool comparisons.
- The WinForms sample Release build passed with no warnings or errors.

This review covers the library's documented object kinds, not every PostgreSQL
feature. RC support remains preview support until the final PostgreSQL 19 release.
