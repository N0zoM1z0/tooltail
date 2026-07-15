namespace Tooltail.Infrastructure.Sqlite.Migrations;

internal sealed record SqliteMigration(
    int Version,
    string ResourceName,
    string Sql,
    string Checksum,
    bool RequiresBackup);
