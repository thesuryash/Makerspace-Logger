using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace LocalWebAdapter;

internal static class Program
{
    private const string BaseUrl = "http://127.0.0.1:5057";

    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("LocalHtmlPolicy", policy => policy
                .AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod());
        });

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        var app = builder.Build();

        var dbPath = ResolveDbPath();
        EnsureDatabaseReady(dbPath);
        WriteRealtimeCsvSnapshot(dbPath);

        app.UseCors("LocalHtmlPolicy");
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/api/health", () => Results.Ok(new { ok = true, dbPath }));

        app.MapPost("/api/admin/init-db", () =>
        {
            EnsureDatabaseReady(dbPath);
            WriteRealtimeCsvSnapshot(dbPath);
            return Results.Ok(new { ok = true, dbPath });
        });

        app.MapGet("/api/locations", () =>
        {
            using var conn = OpenConnection(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Name FROM Locations ORDER BY Name;";

            var locations = new List<object>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                locations.Add(new
                {
                    id = reader.GetString(0),
                    name = reader.GetString(1)
                });
            }

            return Results.Ok(locations);
        });

        app.MapPost("/api/locations", async (HttpRequest request) =>
        {
            var body = await request.ReadFromJsonAsync<CreateLocationRequest>();
            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { error = "name is required." });

            using var conn = OpenConnection(dbPath);
            var id = Guid.NewGuid().ToString();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Locations (Id, Name, Capacity) VALUES ($id, $name, NULL);";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", body.Name.Trim());
            cmd.ExecuteNonQuery();
            WriteRealtimeCsvSnapshot(dbPath);

            return Results.Ok(new { id, name = body.Name.Trim() });
        });

        app.MapGet("/api/events", (int? take) =>
        {
            var rowLimit = Math.Clamp(take ?? 200, 1, 500);
            using var conn = OpenConnection(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT e.Id, e.TimestampUtc, e.EventType, e.ScannedStudentId,
       e.NameAtScan, COALESCE(l.Name, '?') AS LocationName
FROM Events e
LEFT JOIN Locations l ON l.Id = e.LocationId
ORDER BY e.TimestampUtc DESC
LIMIT $take;";
            cmd.Parameters.AddWithValue("$take", rowLimit);

            var events = new List<object>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var utcRaw = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var utc = ParseUtc(utcRaw);

                events.Add(new
                {
                    id = reader.GetString(0),
                    timestampUtc = utc,
                    timestampLocal = utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    eventType = reader.GetString(2),
                    scannedStudentId = reader.GetString(3),
                    nameAtScan = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    locationName = reader.GetString(5)
                });
            }

            return Results.Ok(events);
        });

        app.MapGet("/api/summary", (string? locationId) =>
        {
            using var conn = OpenConnection(dbPath);
            var selectedLocation = string.IsNullOrWhiteSpace(locationId) ? FirstLocationId(conn) : locationId;

            if (string.IsNullOrWhiteSpace(selectedLocation))
                return Results.Ok(new { eventCount = 0, uniqueStudents = 0, lastEventLocal = (string?)null, locationId = (string?)null });

            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM Events WHERE LocationId = $loc;";
            countCmd.Parameters.AddWithValue("$loc", selectedLocation);

            using var uniqCmd = conn.CreateCommand();
            uniqCmd.CommandText = "SELECT COUNT(DISTINCT ScannedStudentId) FROM Events WHERE LocationId = $loc;";
            uniqCmd.Parameters.AddWithValue("$loc", selectedLocation);

            using var lastCmd = conn.CreateCommand();
            lastCmd.CommandText = "SELECT TimestampUtc FROM Events WHERE LocationId = $loc ORDER BY TimestampUtc DESC LIMIT 1;";
            lastCmd.Parameters.AddWithValue("$loc", selectedLocation);

            var eventCount = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);
            var uniqueStudents = Convert.ToInt32(uniqCmd.ExecuteScalar() ?? 0);
            var lastRaw = lastCmd.ExecuteScalar()?.ToString();
            var lastEventLocal = string.IsNullOrWhiteSpace(lastRaw)
                ? null
                : ParseUtc(lastRaw).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            return Results.Ok(new { eventCount, uniqueStudents, lastEventLocal, locationId = selectedLocation });
        });

        app.MapPost("/api/events", async (HttpRequest request) =>
        {
            var body = await request.ReadFromJsonAsync<RecordEventRequest>();
            if (body is null || string.IsNullOrWhiteSpace(body.StudentId))
                return Results.BadRequest(new { error = "studentId is required." });

            const string type = "log";

            using var conn = OpenConnection(dbPath);
            using var tx = conn.BeginTransaction();

            var normalizedId = NormalizeScannedId(body.StudentId);
            if (string.IsNullOrWhiteSpace(normalizedId))
                return Results.BadRequest(new { error = "studentId normalization resulted in empty value." });

            var userId = UpsertUser(conn, normalizedId, body.FirstName, body.LastName);
            var locId = string.IsNullOrWhiteSpace(body.LocationId) ? FirstLocationId(conn) : body.LocationId;
            if (string.IsNullOrWhiteSpace(locId))
                return Results.BadRequest(new { error = "No location exists in database." });

            var eventId = Guid.NewGuid().ToString();
            var fullName = $"{body.FirstName?.Trim()} {body.LastName?.Trim()}".Trim();

            using var insert = conn.CreateCommand();
            insert.CommandText = @"
INSERT INTO Events (Id, UserId, ScannedStudentId, NameAtScan, EventType, LocationId, TimestampUtc, RawPayloadJson)
VALUES ($id, $userId, $studentId, $nameAtScan, $eventType, $locationId, $timestamp, $raw);";
            insert.Parameters.AddWithValue("$id", eventId);
            insert.Parameters.AddWithValue("$userId", userId);
            insert.Parameters.AddWithValue("$studentId", normalizedId);
            insert.Parameters.AddWithValue("$nameAtScan", fullName);
            insert.Parameters.AddWithValue("$eventType", type);
            insert.Parameters.AddWithValue("$locationId", locId);
            insert.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$raw", "{}");
            insert.ExecuteNonQuery();

            tx.Commit();
            WriteRealtimeCsvSnapshot(dbPath);
            return Results.Ok(new { id = eventId, studentId = normalizedId, eventType = type, locationId = locId });
        });

        app.MapDelete("/api/events/{id}", (string id) =>
        {
            if (string.IsNullOrWhiteSpace(id))
                return Results.BadRequest(new { error = "id is required." });

            using var conn = OpenConnection(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Events WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", id.Trim());
            var deleted = cmd.ExecuteNonQuery();
            if (deleted == 0)
                return Results.NotFound(new { error = "event not found." });

            WriteRealtimeCsvSnapshot(dbPath);
            return Results.Ok(new { ok = true, deleted });
        });

        app.MapGet("/api/export.csv", () =>
        {
            using var conn = OpenConnection(dbPath);
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("Section,Id,UserId,StudentId,FirstName,LastName,Email,Status,LocationId,LocationName,TimestampUtc,RawPayloadJson");

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT Id, Name, Capacity FROM Locations ORDER BY Name;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    csv.AppendLine(string.Join(",",
                        "location",
                        EscapeCsv(reader.GetString(0)),
                        "",
                        "",
                        "",
                        "",
                        "",
                        "",
                        EscapeCsv(reader.GetString(0)),
                        EscapeCsv(reader.GetString(1)),
                        "",
                        ""));
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT Id, StudentId, FirstName, LastName, Email, Status, CreatedAtUtc FROM Users ORDER BY CreatedAtUtc DESC;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    csv.AppendLine(string.Join(",",
                        "user",
                        EscapeCsv(reader.GetString(0)),
                        "",
                        EscapeCsv(reader.GetString(1)),
                        EscapeCsv(reader.IsDBNull(2) ? "" : reader.GetString(2)),
                        EscapeCsv(reader.IsDBNull(3) ? "" : reader.GetString(3)),
                        EscapeCsv(reader.IsDBNull(4) ? "" : reader.GetString(4)),
                        EscapeCsv(reader.IsDBNull(5) ? "" : reader.GetString(5)),
                        "",
                        "",
                        EscapeCsv(reader.IsDBNull(6) ? "" : reader.GetString(6)),
                        ""));
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT e.Id, e.UserId, e.ScannedStudentId, e.NameAtScan, e.EventType, e.LocationId, e.TimestampUtc, e.RawPayloadJson,
       COALESCE(l.Name, '?') as LocationName
FROM Events e
LEFT JOIN Locations l ON l.Id = e.LocationId
ORDER BY e.TimestampUtc DESC;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    csv.AppendLine(string.Join(",",
                        "event",
                        EscapeCsv(reader.GetString(0)),
                        EscapeCsv(reader.IsDBNull(1) ? "" : reader.GetString(1)),
                        EscapeCsv(reader.IsDBNull(2) ? "" : reader.GetString(2)),
                        "",
                        "",
                        "",
                        "",
                        EscapeCsv(reader.IsDBNull(5) ? "" : reader.GetString(5)),
                        EscapeCsv(reader.GetString(8)),
                        EscapeCsv(reader.IsDBNull(6) ? "" : reader.GetString(6)),
                        EscapeCsv(reader.IsDBNull(7) ? "" : reader.GetString(7))));
                }
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(csv.ToString());
            return Results.File(bytes, "text/csv", $"makerspace-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
        });

        app.MapGet("/api/realtime.csv", () =>
        {
            var csvPath = ResolveRealtimeCsvPath(dbPath);
            if (!File.Exists(csvPath))
                WriteRealtimeCsvSnapshot(dbPath);

            var csv = File.ReadAllText(csvPath);
            return Results.Text(csv, "text/csv");
        });

        app.Run(BaseUrl);
    }

    private static SqliteConnection OpenConnection(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
        conn.Open();
        return conn;
    }

    private static string ResolveDbPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            var dir = Path.Combine(local, "SpaceAccess");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "access.sqlite");
        }

        var fallbackDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".spaceaccess");
        Directory.CreateDirectory(fallbackDir);
        return Path.Combine(fallbackDir, "access.sqlite");
    }

    private static string ResolveRealtimeCsvPath(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath) ?? ".";
        return Path.Combine(dir, "realtime-export.csv");
    }

    private static string FirstLocationId(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Locations ORDER BY Name LIMIT 1;";
        return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    private static DateTime ParseUtc(string raw)
    {
        if (DateTime.TryParse(raw, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return dt;

        return DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
    }

    private static string UpsertUser(SqliteConnection conn, string studentId, string? firstName, string? lastName)
    {
        using var sel = conn.CreateCommand();
        sel.CommandText = "SELECT Id FROM Users WHERE StudentId = $id LIMIT 1;";
        sel.Parameters.AddWithValue("$id", studentId);
        var existingId = sel.ExecuteScalar()?.ToString();

        if (!string.IsNullOrWhiteSpace(existingId))
        {
            using var update = conn.CreateCommand();
            update.CommandText = @"
UPDATE Users
SET FirstName = CASE WHEN $first <> '' THEN $first ELSE FirstName END,
    LastName = CASE WHEN $last <> '' THEN $last ELSE LastName END
WHERE Id = $id;";
            update.Parameters.AddWithValue("$first", firstName?.Trim() ?? string.Empty);
            update.Parameters.AddWithValue("$last", lastName?.Trim() ?? string.Empty);
            update.Parameters.AddWithValue("$id", existingId);
            update.ExecuteNonQuery();

            return existingId;
        }

        var id = Guid.NewGuid().ToString();
        using var ins = conn.CreateCommand();
        ins.CommandText = @"
INSERT INTO Users (Id, StudentId, FirstName, LastName, Email, Status, CreatedAtUtc)
VALUES ($id, $student, $first, $last, '', 'active', $created);";
        ins.Parameters.AddWithValue("$id", id);
        ins.Parameters.AddWithValue("$student", studentId);
        ins.Parameters.AddWithValue("$first", firstName?.Trim() ?? string.Empty);
        ins.Parameters.AddWithValue("$last", lastName?.Trim() ?? string.Empty);
        ins.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        ins.ExecuteNonQuery();
        return id;
    }

    private static string NormalizeScannedId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(digits)) return string.Empty;
        return digits.Length > 2 ? digits[..^2] : digits;
    }

    private static void EnsureDatabaseReady(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? ".");
        using var conn = OpenConnection(dbPath);
        EnsureSchema(conn);
        EnsureSeedLocation(conn);
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Users (
  Id TEXT NOT NULL PRIMARY KEY,
  StudentId TEXT NOT NULL,
  FirstName TEXT NOT NULL DEFAULT '',
  LastName TEXT NOT NULL DEFAULT '',
  Email TEXT NOT NULL DEFAULT '',
  Status TEXT NOT NULL DEFAULT 'active',
  CreatedAtUtc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_StudentId ON Users(StudentId);

CREATE TABLE IF NOT EXISTS Locations (
  Id TEXT NOT NULL PRIMARY KEY,
  Name TEXT NOT NULL,
  Capacity INTEGER NULL
);

CREATE TABLE IF NOT EXISTS Events (
  Id TEXT NOT NULL PRIMARY KEY,
  UserId TEXT NULL,
  ScannedStudentId TEXT NOT NULL,
  NameAtScan TEXT NOT NULL DEFAULT '',
  EventType TEXT NOT NULL,
  LocationId TEXT NOT NULL,
  TimestampUtc TEXT NOT NULL,
  RawPayloadJson TEXT NOT NULL DEFAULT '{}'
);

CREATE INDEX IF NOT EXISTS IX_Events_LocationId_TimestampUtc ON Events(LocationId, TimestampUtc);";
        cmd.ExecuteNonQuery();
    }

    private static void EnsureSeedLocation(SqliteConnection conn)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(1) FROM Locations;";
        var count = Convert.ToInt32(check.ExecuteScalar() ?? 0);
        if (count > 0) return;

        using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO Locations (Id, Name, Capacity) VALUES ($id, $name, $capacity);";
        insert.Parameters.AddWithValue("$id", "11111111-1111-1111-1111-111111111111");
        insert.Parameters.AddWithValue("$name", "Main Space");
        insert.Parameters.AddWithValue("$capacity", 100);
        insert.ExecuteNonQuery();
    }

    private static void WriteRealtimeCsvSnapshot(string dbPath)
    {
        using var conn = OpenConnection(dbPath);
        var csv = new System.Text.StringBuilder();
        csv.AppendLine("EventId,StudentId,NameAtScan,LocationId,LocationName,TimestampUtc");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT e.Id, e.ScannedStudentId, e.NameAtScan, e.LocationId, COALESCE(l.Name, '?'), e.TimestampUtc
FROM Events e
LEFT JOIN Locations l ON l.Id = e.LocationId
ORDER BY e.TimestampUtc DESC;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            csv.AppendLine(string.Join(",",
                EscapeCsv(reader.GetString(0)),
                EscapeCsv(reader.IsDBNull(1) ? "" : reader.GetString(1)),
                EscapeCsv(reader.IsDBNull(2) ? "" : reader.GetString(2)),
                EscapeCsv(reader.IsDBNull(3) ? "" : reader.GetString(3)),
                EscapeCsv(reader.IsDBNull(4) ? "" : reader.GetString(4)),
                EscapeCsv(reader.IsDBNull(5) ? "" : reader.GetString(5))));
        }

        File.WriteAllText(ResolveRealtimeCsvPath(dbPath), csv.ToString());
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var escaped = value.Replace("\"", "\"\"");
        return (escaped.Contains(',') || escaped.Contains('"') || escaped.Contains('\n') || escaped.Contains('\r'))
            ? $"\"{escaped}\""
            : escaped;
    }

    private sealed record RecordEventRequest(
        string StudentId,
        string? FirstName,
        string? LastName,
        string? LocationId);

    private sealed record CreateLocationRequest(
        string Name);
}
