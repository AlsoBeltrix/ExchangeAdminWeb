using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Repository for the AD Attribute Editor allowlist (<c>editable_attribute</c>) and choice
/// legend (<c>attribute_legend</c>). Backs <see cref="ADAttributeEditorService"/>.
///
/// Behavioral parity notes (SqliteConfigStore-Plan §2a):
/// - The allowlist is NULL-on-corrupt (a distinct fail-closed signal), EMPTY when there is no
///   config (a valid "nothing allowlisted" state). The presence marker distinguishes the two so
///   an explicitly-empty allowlist reads back as empty, never null.
/// - Validation that REJECTS a config (denylist removal, contradictory flags, empty Choice list)
///   stays in the service — this repository only stores/loads rows.
/// - The legend is fail-open: absent or unreadable yields an empty legend.
/// </summary>
public sealed class AttributeEditorRepository
{
    private readonly IConfigStore _store;

    public AttributeEditorRepository(IConfigStore store) => _store = store;

    /// <summary>
    /// Reads the raw allowlist rows and the configured flag in one guarded operation. Returns
    /// false if the read throws (DB-integrity failure → the service treats it as corrupt/null).
    /// </summary>
    public bool TryReadAllowlist(out List<AttributeRow> rows, out bool configured)
    {
        try
        {
            (rows, configured) = _store.Read(connection =>
            {
                var data = ReadAllowlistRows(connection);
                var present = ReadAllowlistConfigured(connection);
                return (data, present);
            });
            return true;
        }
        catch
        {
            rows = new List<AttributeRow>();
            configured = false;
            return false;
        }
    }

    /// <summary>True if the allowlist has been configured (marker set). Used by the importer.</summary>
    public bool TryReadAllowlistConfigured()
    {
        return _store.Read(ReadAllowlistConfigured);
    }

    /// <summary>Replaces the entire allowlist (clear + insert) and marks present, in one txn.</summary>
    public void SaveAllowlist(IReadOnlyList<AttributeRow> rows)
    {
        _store.Write((connection, transaction) =>
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM editable_attribute;";
                delete.ExecuteNonQuery();
            }

            InsertAllowlist(connection, transaction, rows);
            MarkAllowlistPresent(connection, transaction);
        });
    }

    /// <summary>
    /// One-time import of the allowlist only if not yet configured (marker absent). Marks present
    /// even for an empty list. Returns true if it imported (i.e. was not already configured).
    /// </summary>
    public bool ImportAllowlistIfMissing(IReadOnlyList<AttributeRow> rows)
    {
        return _store.Write((connection, transaction) =>
        {
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT 1 FROM editable_attribute_present LIMIT 1;";
                if (check.ExecuteScalar() is not null)
                    return false;
            }

            InsertAllowlist(connection, transaction, rows);
            MarkAllowlistPresent(connection, transaction);
            return true;
        });
    }

    /// <summary>Reads the legend (fail-open: returns empty on any read failure).</summary>
    public Dictionary<string, Dictionary<string, AttributeLegendRow>> ReadLegend()
    {
        try
        {
            return _store.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT attribute_name, choice_value, description, note, source FROM attribute_legend;";
                var map = new Dictionary<string, Dictionary<string, AttributeLegendRow>>(StringComparer.OrdinalIgnoreCase);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var attr = reader.GetString(0);
                    var choice = reader.GetString(1);
                    var desc = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    var note = reader.IsDBNull(3) ? null : reader.GetString(3);
                    var source = reader.IsDBNull(4) ? null : reader.GetString(4);
                    if (!map.TryGetValue(attr, out var inner))
                        map[attr] = inner = new Dictionary<string, AttributeLegendRow>(StringComparer.OrdinalIgnoreCase);
                    inner[choice] = new AttributeLegendRow(desc, note, source);
                }
                return map;
            });
        }
        catch
        {
            return new Dictionary<string, Dictionary<string, AttributeLegendRow>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>True if a legend has been imported/saved (marker set).</summary>
    public bool IsLegendConfigured()
    {
        return _store.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM attribute_legend_present LIMIT 1;";
            return command.ExecuteScalar() is not null;
        });
    }

    /// <summary>One-time import of the legend only if not yet present. Returns true if imported.</summary>
    public bool ImportLegendIfMissing(IReadOnlyDictionary<string, Dictionary<string, AttributeLegendRow>> legend)
    {
        return _store.Write((connection, transaction) =>
        {
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT 1 FROM attribute_legend_present LIMIT 1;";
                if (check.ExecuteScalar() is not null)
                    return false;
            }

            foreach (var (attr, choices) in legend)
            {
                foreach (var (choice, entry) in choices)
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText =
                        "INSERT INTO attribute_legend (attribute_name, choice_value, description, note, source) " +
                        "VALUES ($attr, $choice, $desc, $note, $source) " +
                        "ON CONFLICT(attribute_name, choice_value) DO NOTHING;";
                    insert.Parameters.AddWithValue("$attr", attr);
                    insert.Parameters.AddWithValue("$choice", choice);
                    insert.Parameters.AddWithValue("$desc", (object?)entry.Description ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$note", (object?)entry.Note ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$source", (object?)entry.Source ?? DBNull.Value);
                    insert.ExecuteNonQuery();
                }
            }

            using (var mark = connection.CreateCommand())
            {
                mark.Transaction = transaction;
                mark.CommandText = "INSERT INTO attribute_legend_present (marker) VALUES (1) ON CONFLICT(marker) DO NOTHING;";
                mark.ExecuteNonQuery();
            }
            return true;
        });
    }

    private static List<AttributeRow> ReadAllowlistRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name, label, type, choices_json, required, allow_clear, max_length, pattern, level FROM editable_attribute;";
        var rows = new List<AttributeRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AttributeRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                !reader.IsDBNull(4) && reader.GetInt64(4) != 0,
                !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
                reader.IsDBNull(6) ? null : (int?)reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? 1 : (int)reader.GetInt64(8)));
        }
        return rows;
    }

    private static bool ReadAllowlistConfigured(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM editable_attribute_present LIMIT 1;";
        return command.ExecuteScalar() is not null;
    }

    private static void InsertAllowlist(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<AttributeRow> rows)
    {
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Name))
                continue;

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO editable_attribute (name, label, type, choices_json, required, allow_clear, max_length, pattern, level) " +
                "VALUES ($name, $label, $type, $choices, $required, $allowClear, $maxLength, $pattern, $level) " +
                "ON CONFLICT(name) DO UPDATE SET label=excluded.label, type=excluded.type, choices_json=excluded.choices_json, " +
                "required=excluded.required, allow_clear=excluded.allow_clear, max_length=excluded.max_length, pattern=excluded.pattern, level=excluded.level;";
            insert.Parameters.AddWithValue("$name", row.Name);
            insert.Parameters.AddWithValue("$label", (object?)row.Label ?? DBNull.Value);
            insert.Parameters.AddWithValue("$type", (object?)row.Type ?? DBNull.Value);
            insert.Parameters.AddWithValue("$choices", (object?)row.ChoicesJson ?? DBNull.Value);
            insert.Parameters.AddWithValue("$required", row.Required ? 1 : 0);
            insert.Parameters.AddWithValue("$allowClear", row.AllowClear ? 1 : 0);
            insert.Parameters.AddWithValue("$maxLength", (object?)row.MaxLength ?? DBNull.Value);
            insert.Parameters.AddWithValue("$pattern", (object?)row.Pattern ?? DBNull.Value);
            insert.Parameters.AddWithValue("$level", row.Level);
            insert.ExecuteNonQuery();
        }
    }

    private static void MarkAllowlistPresent(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO editable_attribute_present (marker) VALUES (1) ON CONFLICT(marker) DO NOTHING;";
        command.ExecuteNonQuery();
    }
}

/// <summary>Raw allowlist row as stored (choices serialized to JSON). Decoupled from the service's EditableAttribute.</summary>
public sealed record AttributeRow(
    string Name,
    string Label,
    string Type,
    string? ChoicesJson,
    bool Required,
    bool AllowClear,
    int? MaxLength,
    string? Pattern,
    int Level);

/// <summary>Raw legend row as stored.</summary>
public sealed record AttributeLegendRow(string Description, string? Note, string? Source);
