using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

namespace SpaceAccess.Wpf;

public partial class MainWindow : Window
{
    // track selected location in manage tab
    private Guid? _selectedManageLocationId;

    public MainWindow()
    {
        InitializeComponent();
        AccessContext.EnsureCreatedAndSeed();
        LoadLocations();
        RefreshUi();
    }

    private Guid CurrentLocationId =>
        (Guid)(LocationCombo.SelectedValue ?? AccessContext.SeedLocationId);

    private void LoadLocations()
    {
        using var db = new AccessContext();
        var locations = db.Locations.OrderBy(l => l.Name).ToList();

        // populate dropdown
        LocationCombo.ItemsSource = locations;
        if (LocationCombo.SelectedValue == null && locations.Any())
            LocationCombo.SelectedValue = locations.First().Id;

        // populate manage list (DataGrid)
        LocationsGrid.ItemsSource = locations;
    }

    // called after add/save/delete to refresh both lists and UI
    private void ReloadLocationsAndKeepSelection(Guid? selectId = null)
    {
        LoadLocations();
        if (selectId.HasValue)
        {
            // select in LocationCombo if present
            LocationCombo.SelectedValue = selectId.Value;
        }
        RefreshUi();
    }

    // Manage tab: selection changed
    private void LocationsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LocationsGrid.SelectedItem is Location loc)
        {
            _selectedManageLocationId = loc.Id;
            LocNameBox.Text = loc.Name;
            LocCapacityBox.Text = loc.Capacity?.ToString() ?? "";
        }
        else
        {
            _selectedManageLocationId = null;
            LocNameBox.Text = "";
            LocCapacityBox.Text = "";
        }
    }

    private void AddLocation_Click(object sender, RoutedEventArgs e)
    {
        var name = LocNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Enter a name for the location.");
            return;
        }
        int? cap = null;
        if (int.TryParse(LocCapacityBox.Text.Trim(), out var c)) cap = c;

        using var db = new AccessContext();
        var loc = new Location { Name = name, Capacity = cap };
        db.Locations.Add(loc);
        db.SaveChanges();

        ReloadLocationsAndKeepSelection(loc.Id);
        MessageBox.Show("Location added.");
    }

    private void SaveLocation_Click(object sender, RoutedEventArgs e)
    {
        if (!_selectedManageLocationId.HasValue)
        {
            MessageBox.Show("Select a location to save.");
            return;
        }
        using var db = new AccessContext();
        var loc = db.Locations.Find(_selectedManageLocationId.Value);
        if (loc == null) { MessageBox.Show("Location not found."); return; }

        var name = LocNameBox.Text.Trim();
        loc.Name = name;
        if (int.TryParse(LocCapacityBox.Text.Trim(), out var c)) loc.Capacity = c;
        else loc.Capacity = null;

        db.SaveChanges();
        ReloadLocationsAndKeepSelection(loc.Id);
        MessageBox.Show("Location updated.");
    }

    private void DeleteLocation_Click(object sender, RoutedEventArgs e)
    {
        if (!_selectedManageLocationId.HasValue)
        {
            MessageBox.Show("Select a location to delete.");
            return;
        }

        // prevent deleting the seeded default if desired
        if (_selectedManageLocationId.Value == AccessContext.SeedLocationId)
        {
            MessageBox.Show("Cannot delete the seed location.");
            return;
        }

        if (MessageBox.Show("Delete selected location?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        using var db = new AccessContext();
        var loc = db.Locations.Find(_selectedManageLocationId.Value);
        if (loc != null)
        {
            db.Locations.Remove(loc);
            db.SaveChanges();
        }

        ReloadLocationsAndKeepSelection();
        MessageBox.Show("Location deleted.");
    }

    private void RefreshUi()
    {
        using var db = new AccessContext();

        var q = db.Events
            .Include(e => e.Location)
            .Include(e => e.User)
            .OrderByDescending(e => e.TimestampUtc)
            .Take(200)
            .AsEnumerable()
            .Select(e => new ScanEventView
            {
                Id = e.Id,
                TimestampLocal = e.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                EventType = e.EventType,
                ScannedStudentId = e.ScannedStudentId,
                NameAtScan = string.IsNullOrWhiteSpace(e.NameAtScan) ? $"{e.User?.FirstName} {e.User?.LastName}".Trim() : e.NameAtScan,
                LocationName = e.Location?.Name ?? "?"
            })
            .ToList();
    EventsGrid.ItemsSource = q;
    // allow edits to flow through
    EventsGrid.CellEditEnding -= EventsGrid_CellEditEnding;
    EventsGrid.CellEditEnding += EventsGrid_CellEditEnding;

        var lastByUser = db.Events
            .Where(e => e.LocationId == CurrentLocationId)
            .GroupBy(e => e.ScannedStudentId)
            .Select(g => g.OrderByDescending(x => x.TimestampUtc).First())
            .ToList();

        var occupancy = lastByUser.Count(e => e.EventType == "entry");
        var cap = db.Locations.Find(CurrentLocationId)?.Capacity;

        OccupancyText.Text = occupancy.ToString();
        CapacityText.Text = cap?.ToString() ?? "—";
        StatusText.Text = (cap.HasValue && occupancy > cap.Value) ? "OVER CAPACITY" : "OK";
    }

    private (User user, bool created) GetOrCreateUser(AccessContext db, string studentId, string first, string last)
    {
        var user = db.Users.FirstOrDefault(u => u.StudentId == studentId);
        if (user == null)
        {
            user = new User { StudentId = studentId, FirstName = first, LastName = last };
            db.Users.Add(user);
            db.SaveChanges();
            return (user, true);
        }
        if (!string.IsNullOrWhiteSpace(first)) user.FirstName = first;
        if (!string.IsNullOrWhiteSpace(last)) user.LastName = last;
        db.SaveChanges();
        return (user, false);
    }

    private string NormalizeScannedId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var s = raw.Trim();
        // if it ends with two digits and total length > 2, trim the last two digits
        if (s.Length > 2 && int.TryParse(s[^2..], out _))
        {
            return s[..^2];
        }
        return s;
    }

    private void RecordEvent(string type, string? overrideStudentId = null)
    {
        var idRaw = overrideStudentId ?? StudentIdBox.Text;
        var id = NormalizeScannedId(idRaw);
        var first = FirstBox.Text.Trim();
        var last = LastBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(id))
        {
            MessageBox.Show("Enter Student ID.");
            return;
        }

        using var db = new AccessContext();
        var (user, _) = GetOrCreateUser(db, id, first, last);

        var ev = new ScanEvent
        {
            UserId = user.Id,
            ScannedStudentId = user.StudentId,
            NameAtScan = $"{user.FirstName} {user.LastName}".Trim(),
            EventType = type,
            LocationId = CurrentLocationId,
            TimestampUtc = DateTime.UtcNow
        };
        db.Events.Add(ev);
        db.SaveChanges();

    RefreshUi();
    // clear the scanned id to avoid accidental duplicate entries and keep focus
    StudentIdBox.Text = string.Empty;
    StudentIdBox.Focus();
    }

    private void CheckInBtn_Click(object sender, RoutedEventArgs e) => RecordEvent("entry");

    private void StudentIdBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            // when scanner appends Enter, record event using the scanned value
            RecordEvent("entry", StudentIdBox.Text);
            e.Handled = true;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        using var db = new AccessContext();
        var events = db.Events.Include(e => e.Location).Include(e => e.User)
            .OrderByDescending(e => e.TimestampUtc)
            .Select(e => new
            {
                TimestampUtc = e.TimestampUtc.ToString("o"),
                EventType = e.EventType,
                StudentId = e.ScannedStudentId,
                FirstName = e.User != null ? e.User.FirstName : "",
                LastName = e.User != null ? e.User.LastName : "",
                Location = e.Location != null ? e.Location.Name : ""
            })
            .ToList();

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "access_log.csv");
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture));
        csv.WriteRecords(events);
        MessageBox.Show($"Exported to {path}");
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshUi();

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        using var db = new AccessContext();
        var query = db.Events.Include(e => e.Location).Include(e => e.User).AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(e =>
                e.ScannedStudentId.Contains(q) ||
                (e.User != null && (
                   e.User.FirstName.Contains(q) ||
                   e.User.LastName.Contains(q)
                )) ||
                e.NameAtScan.Contains(q));
        }

        var list = query.OrderByDescending(e => e.TimestampUtc).Take(200)
            .AsEnumerable()
            .Select(e => new
            {
                TimestampLocal = e.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                e.EventType,
                e.ScannedStudentId,
                NameAtScan = string.IsNullOrWhiteSpace(e.NameAtScan) ? $"{e.User?.FirstName} {e.User?.LastName}".Trim() : e.NameAtScan,
                LocationName = e.Location?.Name ?? "?"
            }).ToList();

        EventsGrid.ItemsSource = list;
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        RefreshUi();
    }

    // Select the row under mouse when user right-clicks so ContextMenu acts on that item
    private void EventsGrid_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var dep = (System.Windows.DependencyObject)e.OriginalSource;
        while (dep != null && !(dep is System.Windows.Controls.DataGridRow))
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        if (dep is System.Windows.Controls.DataGridRow row)
        {
            row.IsSelected = true;
        }
    }

    private void DeleteEntry_Click(object sender, RoutedEventArgs e)
    {
        if (EventsGrid.SelectedItem is not ScanEventView sel) {
            MessageBox.Show("Select an event to delete.");
            return;
        }

        if (MessageBox.Show("Delete selected event?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        using var db = new AccessContext();
        var ev = db.Events.Find(sel.Id);
        if (ev != null) {
            db.Events.Remove(ev);
            db.SaveChanges();
        }

        RefreshUi();
    }

    private void EventsGrid_CellEditEnding(object? sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.Item is not ScanEventView view) return;

        // Get edited textbox value
        if (e.EditingElement is System.Windows.Controls.TextBox tb)
        {
            var newVal = tb.Text.Trim();
            using var db = new AccessContext();
            var ev = db.Events.Find(view.Id);
            if (ev != null)
            {
                ev.NameAtScan = newVal;
                db.SaveChanges();

                // If this event links to a user, optionally update their first/last name
                if (ev.UserId.HasValue)
                {
                    var user = db.Users.Find(ev.UserId.Value);
                    if (user != null)
                    {
                        var parts = newVal.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 1) user.FirstName = parts[0];
                        if (parts.Length == 2) user.LastName = parts[1];
                        db.SaveChanges();
                    }
                }
            }
        }

        // Refresh UI to show persisted values
        RefreshUi();
    }
}
