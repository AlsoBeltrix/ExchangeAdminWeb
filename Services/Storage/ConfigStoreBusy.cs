using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Whether a config-store call failed because another connection held the database's write lock for longer than the store
/// waits for it (aud-a28v): the one failure a retry is expected to clear, so a page can say "busy, try again" for it and for
/// nothing else.
/// </summary>
/// <remarks>
/// <para>The wait is about 35 seconds, measured: the connection factory's <c>busy_timeout=5000</c> inside each step of
/// Microsoft.Data.Sqlite's own busy retry, which runs until the command's default 30-second timeout. A lock still held then
/// surfaces from <c>BEGIN IMMEDIATE</c> as a <see cref="SqliteException"/> whose result code is SQLITE_BUSY (5) - "SQLite
/// Error 5: 'database is locked'.", extended code 5, no inner exception.</para>
/// <para>SQLITE_LOCKED (6) is not counted: with the private cache <see cref="SqliteConnectionFactory"/> opens, it is a
/// conflict inside one connection, not another writer. No other code is counted either: a corrupt or unreadable store must
/// not be told to try again. The result code decides, never the message.</para>
/// </remarks>
public static class ConfigStoreBusy
{
    // SQLITE_BUSY. Its extended forms (BUSY_RECOVERY 261, BUSY_SNAPSHOT 517, BUSY_TIMEOUT 773) carry it in their low byte.
    private const int SqliteBusy = 5;

    /// <summary>True for a <see cref="SqliteException"/> whose primary result code is SQLITE_BUSY; false for anything else.</summary>
    public static bool IsBusy(Exception exception) =>
        exception is SqliteException sqlite && (sqlite.SqliteErrorCode & 0xFF) == SqliteBusy;
}
