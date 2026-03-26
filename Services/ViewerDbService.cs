using Microsoft.Data.Sqlite;
using HomelabCountdown.Models;

namespace HomelabCountdown.Services;

/// <summary>
/// Persists daily viewer counts in a SQLite database at the art-cache path.
/// DB file: /var/lib/homecountdown/art-cache/countdown.db
/// </summary>
public class ViewerDbService
{
    private readonly string _dbPath;
    private readonly ILogger<ViewerDbService> _logger;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public ViewerDbService(IConfiguration config, ILogger<ViewerDbService> logger)
    {
        var cachePath = config["ArtCache:Path"] is { Length: > 0 } p
            ? p : Path.Combine(AppContext.BaseDirectory, "art-cache");
        Directory.CreateDirectory(cachePath);
        _dbPath = Path.Combine(cachePath, "countdown.db");
        _logger = logger;
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    public async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS viewer_counts (
                    date        TEXT    NOT NULL,
                    key         TEXT    NOT NULL,
                    country_code TEXT   NOT NULL,
                    country_name TEXT   NOT NULL,
                    state_code  TEXT,
                    state_name  TEXT,
                    count       INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (date, key)
                )
                """;
            await cmd.ExecuteNonQueryAsync();
            _initialized = true;
            _logger.LogInformation("SQLite viewer DB initialized at {Path}", _dbPath);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Increments the view count for a location on the given date.</summary>
    public async Task UpsertVisitAsync(DateOnly date, ViewerEntry entry)
    {
        await EnsureInitializedAsync();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO viewer_counts (date, key, country_code, country_name, state_code, state_name, count)
            VALUES ($date, $key, $cc, $cn, $sc, $sn, 1)
            ON CONFLICT (date, key) DO UPDATE SET count = count + 1
            """;
        cmd.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$key",  entry.Key);
        cmd.Parameters.AddWithValue("$cc",   entry.CountryCode);
        cmd.Parameters.AddWithValue("$cn",   entry.CountryName);
        cmd.Parameters.AddWithValue("$sc",   (object?)entry.StateCode  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sn",   (object?)entry.StateName  ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Returns all viewer rows for a specific date, ordered by count desc.</summary>
    public async Task<List<ViewerEntry>> GetViewersForDateAsync(DateOnly date)
    {
        await EnsureInitializedAsync();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT key, country_code, country_name, state_code, state_name, count
            FROM   viewer_counts
            WHERE  date = $date
            ORDER  BY count DESC
            """;
        cmd.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));

        var results = new List<ViewerEntry>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ViewerEntry
            {
                Key         = reader.GetString(0),
                CountryCode = reader.GetString(1),
                CountryName = reader.GetString(2),
                StateCode   = reader.IsDBNull(3) ? null : reader.GetString(3),
                StateName   = reader.IsDBNull(4) ? null : reader.GetString(4),
                Count       = reader.GetInt32(5)
            });
        }
        return results;
    }

    /// <summary>Total views for one date.</summary>
    public async Task<int> GetTotalViewsForDateAsync(DateOnly date)
    {
        await EnsureInitializedAsync();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(count),0) FROM viewer_counts WHERE date = $date";
        cmd.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    /// <summary>Total views grouped by date — used by the gallery to show per-day counts.</summary>
    public async Task<Dictionary<DateOnly, int>> GetTotalViewsByDateAsync()
    {
        await EnsureInitializedAsync();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT date, SUM(count) FROM viewer_counts GROUP BY date";

        var result = new Dictionary<DateOnly, int>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (DateOnly.TryParse(reader.GetString(0), out var d))
                result[d] = Convert.ToInt32(reader.GetValue(1));
        }
        return result;
    }
}
