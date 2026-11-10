using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace BeautyBrowser.Services;

/// <summary>Local SQLite store for history and settings, at %LOCALAPPDATA%\BeautyBrowser\browser.db.</summary>
public sealed class DatabaseService
{
    public static string DatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BeautyBrowser", "browser.db");

    private readonly string _connectionString;

    public DatabaseService()
    {
        var dir = Path.GetDirectoryName(DatabasePath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                url TEXT UNIQUE NOT NULL,
                title TEXT,
                favicon_url TEXT,
                last_visit TEXT NOT NULL,
                visit_count INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS idx_history_last_visit ON history(last_visit DESC);

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL
            );

            INSERT OR IGNORE INTO settings (key, value) VALUES ('search_engine', 'Google');
            INSERT OR IGNORE INTO settings (key, value) VALUES ('home_url', 'https://www.google.com');
        ";
        cmd.ExecuteNonQuery();
    }


    public void RecordVisit(string url, string? title = null, string? faviconUrl = null)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO history (url, title, favicon_url, last_visit, visit_count)
            VALUES (@url, @title, @favicon, @now, 1)
            ON CONFLICT(url) DO UPDATE SET
                title = COALESCE(NULLIF(@title, ''), history.title),
                favicon_url = COALESCE(@favicon, history.favicon_url),
                last_visit = @now,
                visit_count = history.visit_count + 1;
        ";
        cmd.Parameters.AddWithValue("@url", url);
        cmd.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@favicon", (object?)faviconUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public void UpdateDescription(string url, string title, string? faviconUrl)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE history SET
                title = CASE WHEN @title <> '' THEN @title ELSE title END,
                favicon_url = COALESCE(@favicon, favicon_url)
            WHERE url = @url;
        ";
        cmd.Parameters.AddWithValue("@url", url);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@favicon", (object?)faviconUrl ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<HistoryEntry> GetRecentHistory(int limit = 100)
    {
        var list = new List<HistoryEntry>();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT url, title, favicon_url, last_visit, visit_count
            FROM history
            ORDER BY last_visit DESC
            LIMIT @limit;
        ";
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadEntry(reader));
        }
        return list;
    }

    public List<HistoryEntry> SearchHistory(string query, int limit = 50)
    {
        var list = new List<HistoryEntry>();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT url, title, favicon_url, last_visit, visit_count
            FROM history
            WHERE url LIKE @q OR title LIKE @q
            ORDER BY visit_count DESC, last_visit DESC
            LIMIT @limit;
        ";
        cmd.Parameters.AddWithValue("@q", $"%{query}%");
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadEntry(reader));
        }
        return list;
    }

    public void DeleteHistoryEntry(string url)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM history WHERE url = @url;";
        cmd.Parameters.AddWithValue("@url", url);
        cmd.ExecuteNonQuery();
    }

    public void ClearAllHistory()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM history;";
        cmd.ExecuteNonQuery();
    }

    private static HistoryEntry ReadEntry(SqliteDataReader reader)
    {
        var url = reader.GetString(0);
        var title = reader.IsDBNull(1) ? url : reader.GetString(1);
        var favicon = reader.IsDBNull(2) ? null : reader.GetString(2);
        var lastVisitStr = reader.GetString(3);
        var visitCount = reader.GetInt32(4);

        DateTime.TryParse(lastVisitStr, out var lastVisit);
        return new HistoryEntry
        {
            Url = url,
            Title = string.IsNullOrWhiteSpace(title) ? url : title,
            FaviconUrl = favicon,
            LastVisit = lastVisit.ToLocalTime(),
            VisitCount = visitCount
        };
    }


    public string GetSetting(string key, string defaultValue = "")
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = @key LIMIT 1;";
        cmd.Parameters.AddWithValue("@key", key);

        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? defaultValue;
    }

    public void SetSetting(string key, string value)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO settings (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = @value;
        ";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes every setting, so the defaults come back.</summary>
    public void ClearSettings()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM settings;";
        cmd.ExecuteNonQuery();
    }
}
