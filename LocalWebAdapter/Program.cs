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
        if (!File.Exists(dbPath))
        {
            Console.WriteLine($"[adapter] SQLite DB was not found at: {dbPath}");
            Console.WriteLine("[adapter] Start the WPF app once so it creates the DB, then restart this adapter.");
        }

        app.UseCors("LocalHtmlPolicy");
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/api/health", () => Results.Ok(new { ok = true, dbPath }));

        app.MapGet("/api/locations", () =>
        {
            using var conn = OpenConnection(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, Capacity FROM Locations ORDER BY Name;";

            var locations = new List<object>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                locations.Add(new
                {
                    id = reader.GetString(0),
                    name = reader.GetString(1),
                    capacity = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2)
                });
            }

            return Results.Ok(locations);
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
                return Results.Ok(new { occupancy = 0, capacity = (int?)null, status = "NO_LOCATION" });

            using var capCmd = conn.CreateCommand();
            capCmd.CommandText = "SELECT Capacity FROM Locations WHERE Id = $id LIMIT 1;";
            capCmd.Parameters.AddWithValue("$id", selectedLocation);
            var capObj = capCmd.ExecuteScalar();
            int? capacity = capObj is null || capObj == DBNull.Value ? null : Convert.ToInt32(capObj);

            using var occCmd = conn.CreateCommand();
            occCmd.CommandText = @"
WITH last_by_user AS (
  SELECT ScannedStudentId, MAX(TimestampUtc) AS Latest
  FROM Events
  WHERE LocationId = $loc
  GROUP BY ScannedStudentId
)
SELECT COUNT(*)
FROM last_by_user l
JOIN Events e ON e.ScannedStudentId = l.ScannedStudentId AND e.TimestampUtc = l.Latest
WHERE e.EventType = 'entry' AND e.LocationId = $loc;";
            occCmd.Parameters.AddWithValue("$loc", selectedLocation);

            var occupancy = Convert.ToInt32(occCmd.ExecuteScalar() ?? 0);
            var status = capacity.HasValue && occupancy > capacity.Value ? "OVER CAPACITY" : "OK";

            return Results.Ok(new { occupancy, capacity, status, locationId = selectedLocation });
        });

        app.MapPost("/api/events", async (HttpRequest request) =>
        {
            var body = await request.ReadFromJsonAsync<RecordEventRequest>();
            if (body is null || string.IsNullOrWhiteSpace(body.StudentId))
                return Results.BadRequest(new { error = "studentId is required." });

            var type = string.IsNullOrWhiteSpace(body.EventType) ? "entry" : body.EventType.Trim().ToLowerInvariant();
            if (type is not ("entry" or "exit" or "manual"))
                return Results.BadRequest(new { error = "eventType must be entry, exit, or manual." });

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
            return Results.Ok(new { id = eventId, studentId = normalizedId, eventType = type, locationId = locId });
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
            return Path.Combine(local, "SpaceAccess", "access.sqlite");

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".spaceaccess", "access.sqlite");
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

    private sealed record RecordEventRequest(
        string StudentId,
        string? FirstName,
        string? LastName,
        string? EventType,
        string? LocationId);
}
