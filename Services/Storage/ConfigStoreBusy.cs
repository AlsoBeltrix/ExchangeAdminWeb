using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

public static class ConfigStoreBusy
{
    private const int SqliteBusy = 5;

    public static bool IsBusy(Exception exception) =>
        exception is SqliteException sqlite && (sqlite.SqliteErrorCode & 0xFF) == SqliteBusy;
}
