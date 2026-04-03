using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
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
            nameAtScan = reader.IsDBNull(4) ? "" : reader.GetString(4),
            locationName = reader.GetString(5)
        });
    }

    return Results.Ok(events);
});

app.MapGet("/api/summary", (string? locationId) =>
{
    using var conn = OpenConnection(dbPath);
    var selectedLocation = string.IsNullOrWhiteSpace(locationId) ? FirstLocationId(conn) : locationId!;

    if (string.IsNullOrWhiteSpace(selectedLocation))
    {
        return Results.Ok(new { occupancy = 0, capacity = (int?)null, status = "NO_LOCATION" });
    }

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
    var locId = string.IsNullOrWhiteSpace(body.LocationId) ? FirstLocationId(conn) : body.LocationId!;
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

app.MapGet("/", () => Results.Content(HtmlPage, "text/html"));

app.Run("http://127.0.0.1:5057");

static SqliteConnection OpenConnection(string dbPath)
{
    var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
    conn.Open();
    return conn;
}

static string ResolveDbPath()
{
    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (!string.IsNullOrWhiteSpace(local))
        return Path.Combine(local, "SpaceAccess", "access.sqlite");

    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".spaceaccess", "access.sqlite");
}

static string FirstLocationId(SqliteConnection conn)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id FROM Locations ORDER BY Name LIMIT 1;";
    return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
}

static DateTime ParseUtc(string raw)
{
    if (DateTime.TryParse(raw, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
        return dt;

    return DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
}

static string UpsertUser(SqliteConnection conn, string studentId, string? firstName, string? lastName)
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

static string NormalizeScannedId(string raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

    var digits = new string(raw.Where(char.IsDigit).ToArray());
    if (string.IsNullOrWhiteSpace(digits)) return string.Empty;
    return digits.Length > 2 ? digits[..^2] : digits;
}

internal sealed record RecordEventRequest(
    string StudentId,
    string? FirstName,
    string? LastName,
    string? EventType,
    string? LocationId);

const string HtmlPage = """
<!doctype html>
<html>
<head>
  <meta charset=\"utf-8\" />
  <title>Makerspace Logger Local Adapter</title>
  <style>
    body { font-family: Arial, sans-serif; margin: 24px; }
    .row { display: flex; gap: 12px; margin-bottom: 10px; flex-wrap: wrap; }
    input, select, button { padding: 8px; }
    table { border-collapse: collapse; width: 100%; margin-top: 20px; }
    th, td { border: 1px solid #ddd; padding: 8px; text-align: left; }
    th { background: #f4f4f4; }
    .meta { margin: 12px 0; font-weight: 600; }
  </style>
</head>
<body>
  <h2>Makerspace Logger — Temporary Local Web Adapter</h2>
  <p>Use this from localhost while installer/security clearance is pending.</p>

  <div class=\"row\">
    <input id=\"studentId\" placeholder=\"Student ID\" />
    <input id=\"firstName\" placeholder=\"First Name (optional)\" />
    <input id=\"lastName\" placeholder=\"Last Name (optional)\" />
    <select id=\"eventType\"><option value=\"entry\">entry</option><option value=\"exit\">exit</option><option value=\"manual\">manual</option></select>
    <button onclick=\"submitEvent()\">Record Event</button>
  </div>

  <div class=\"meta\" id=\"summary\">Loading summary...</div>

  <table>
    <thead><tr><th>When (Local)</th><th>Type</th><th>Student ID</th><th>Name</th><th>Location</th></tr></thead>
    <tbody id=\"events\"></tbody>
  </table>

<script>
async function fetchSummary() {
  const s = await fetch('/api/summary').then(r => r.json());
  document.getElementById('summary').innerText = `Occupancy: ${s.occupancy} | Capacity: ${s.capacity ?? '—'} | Status: ${s.status}`;
}

async function fetchEvents() {
  const rows = await fetch('/api/events?take=100').then(r => r.json());
  document.getElementById('events').innerHTML = rows.map(r =>
    `<tr><td>${r.timestampLocal}</td><td>${r.eventType}</td><td>${r.scannedStudentId}</td><td>${r.nameAtScan || ''}</td><td>${r.locationName}</td></tr>`
  ).join('');
}

async function submitEvent() {
  const payload = {
    studentId: document.getElementById('studentId').value,
    firstName: document.getElementById('firstName').value,
    lastName: document.getElementById('lastName').value,
    eventType: document.getElementById('eventType').value
  };

  const resp = await fetch('/api/events', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  });

  if (!resp.ok) {
    const err = await resp.json();
    alert(err.error || 'Failed to record event');
    return;
  }

  document.getElementById('studentId').value = '';
  await fetchSummary();
  await fetchEvents();
}

fetchSummary();
fetchEvents();
setInterval(() => { fetchSummary(); fetchEvents(); }, 5000);
</script>
</body>
</html>
""";
