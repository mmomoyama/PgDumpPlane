// テストから内部型へアクセスし、公開APIを増やさずに検証します。
// Allows tests to inspect internal types without expanding the public API.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PgDumpPlane.Tests")]
