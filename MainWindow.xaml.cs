using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
// using System.Windows.Media; // removed logo generation
// using System.Windows.Media.Imaging;
using System.Windows;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows.Interop;
using System.Windows.Controls;

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

    // keyboard hook fields
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private static IntPtr _hookId = IntPtr.Zero;
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private LowLevelKeyboardProc? _proc = null;
    private StringBuilder _kbBuffer = new StringBuilder();

    // Raw Input interop and device selection
    private HwndSource? _hwndSource;
    private const int WM_INPUT = 0x00FF;
    private bool _rawInputRegistered = false;
    // we no longer rely on deferred device initialization; mark initialized so checkbox works
    private bool _initializedDevices = true;

    private record RawDeviceInfo
    {
        public IntPtr DeviceHandle { get; init; }
        public string DisplayName { get; init; } = string.Empty;
    }

    private List<RawDeviceInfo> _rawDevices = new List<RawDeviceInfo>();
    private Dictionary<IntPtr, StringBuilder> _rawBuffers = new Dictionary<IntPtr, StringBuilder>();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private void StartHook()
    {
        if (_hookId != IntPtr.Zero) return;
        _proc = HookCallback;
        try
        {
            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            var moduleName = curProcess.MainModule?.ModuleName;
            var moduleHandle = GetModuleHandle(moduleName ?? string.Empty);
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc!, moduleHandle, 0);
        }
        catch
        {
            // best-effort; if we can't get module handle, try without it
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc!, IntPtr.Zero, 0);
        }
    }

    private void StopHook()
    {
        if (_hookId == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    // Register for Raw Input (keyboard-ish devices)
    private bool RegisterRawInput()
    {
        // Ensure we have an HwndSource to receive messages
        var helper = new WindowInteropHelper(this);
        var hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero)
        {
            this.SourceInitialized += OnSourceInitialized_RegisterRaw;
            return false;
        }

        // ensure we have a single HwndSource hook
        if (_hwndSource == null)
        {
            _hwndSource = HwndSource.FromHwnd(hwnd);
            if (_hwndSource != null)
                _hwndSource.AddHook(WndProc);
        }

        // register for keyboard raw input from all keyboards
        var rid = new RAWINPUTDEVICE[1];
        rid[0].usUsagePage = 0x01; // Generic desktop controls
        rid[0].usUsage = 0x06; // Keyboard
        rid[0].dwFlags = RIDEV_INPUTSINK; // receive even when not focused
        rid[0].hwndTarget = hwnd;
    // try to register
    var ok = RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    _rawInputRegistered = ok;
        if (!ok)
        {
            MessageBox.Show("Failed to register Raw Input devices. Ensure the application has an HWND and try Refresh.\nIf this keeps failing, the scanner may not expose a keyboard HID device.", "Raw Input Registration", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            // success
        }

        return _rawInputRegistered;
    }

    private void OnSourceInitialized_RegisterRaw(object? s, EventArgs e)
    {
        RegisterRawInput();
        this.SourceInitialized -= OnSourceInitialized_RegisterRaw;
    }

    private void OnSourceInitialized_InitDevices(object? s, EventArgs e)
    {
        try
        {
            RefreshDevices();
            _initializedDevices = true;
            if (this.FindName("BackgroundListenCheck") is System.Windows.Controls.CheckBox cb && cb.IsChecked == true)
                RegisterRawInput();
        }
        catch { }
        this.SourceInitialized -= OnSourceInitialized_InitDevices;
    }

    private void UnregisterRawInput()
    {
        if (_hwndSource != null)
        {
            try { _hwndSource.RemoveHook(WndProc); } catch { }
            _hwndSource = null;
        }

        if (_rawInputRegistered)
        {
            // use RIDEV_REMOVE to unregister keyboards
            var rid = new RAWINPUTDEVICE[1];
            rid[0].usUsagePage = 0x01;
            rid[0].usUsage = 0x06;
            rid[0].dwFlags = RIDEV_REMOVE;
            rid[0].hwndTarget = IntPtr.Zero;
            try
            {
                RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            }
            catch { }
            _rawInputRegistered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT)
        {
            HandleRawInput(lParam);
            handled = false; // allow others to process
        }
        return IntPtr.Zero;
    }

    // Raw input handling removed — no-op since device picker UI was removed
    private void HandleRawInput(IntPtr lParam)
    {
        // Intentionally empty
        return;
    }

    private void RefreshDevices()
    {
        // Device enumeration removed — keep empty to avoid referencing removed UI controls.
        _rawDevices.Clear();
        return;
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    // Raw Input interop constants/types
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const int RIM_TYPEKEYBOARD = 1;
    private const int RIM_TYPEMOUSE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetRawInputDeviceList([In, Out] RAWINPUTDEVICELIST[] pRawInputDeviceList, out uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetRawInputDeviceList(IntPtr pRawInputDeviceList, out uint puiNumDevices, uint cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWKEYBOARD
    {
        [FieldOffset(0)] public ushort MakeCode;
        [FieldOffset(2)] public ushort Flags;
        [FieldOffset(4)] public ushort Reserved;
        [FieldOffset(6)] public ushort VKey;
        [FieldOffset(8)] public uint Message;
        [FieldOffset(12)] public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWMOUSE
    {
        // not used
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWINPUT
    {
        [FieldOffset(0)] public RAWINPUTHEADER header;
        [FieldOffset(16)] public RAWMOUSE mouse;
        [FieldOffset(16)] public RAWKEYBOARD keyboard;
        // Note: layout simplified for keyboard extraction
        public RAWKEYBOARD data => keyboard;
    }

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, StringBuilder pData, ref uint pcbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(vkCode);
            var ch = VkToChar((System.Windows.Input.Key)key);
            if (ch != '\0')
            {
                if (ch == '\r' || ch == '\n')
                {
                    var raw = _kbBuffer.ToString();
                    _kbBuffer.Clear();
                    var cleaned = Regex.Replace(raw, "\\s+", "");
                    Dispatcher.Invoke(() => RecordEvent("entry", cleaned));
                }
                else
                {
                    _kbBuffer.Append(ch);
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private char VkToChar(System.Windows.Input.Key key)
    {
        if (key >= System.Windows.Input.Key.D0 && key <= System.Windows.Input.Key.D9)
            return (char)('0' + (key - System.Windows.Input.Key.D0));
        if (key >= System.Windows.Input.Key.NumPad0 && key <= System.Windows.Input.Key.NumPad9)
            return (char)('0' + (key - System.Windows.Input.Key.NumPad0));
        if (key >= System.Windows.Input.Key.A && key <= System.Windows.Input.Key.Z)
            return (char)('A' + (key - System.Windows.Input.Key.A));
        if (key == System.Windows.Input.Key.Space) return ' ';
        if (key == System.Windows.Input.Key.Enter) return '\r';
        return '\0';
    }

    private void BackgroundListenCheck_Checked(object sender, RoutedEventArgs e)
    {
        // Start the existing global keyboard hook to capture scanner keyboard input in background
        StartHook();
    }

    private void BackgroundListenCheck_Unchecked(object sender, RoutedEventArgs e)
    {
        // Stop the global hook
        StopHook();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopHook();
        UnregisterRawInput();
        base.OnClosed(e);
    }

    // logo generation removed

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
        // remove all non-digit characters to ensure we only store numbers
        var digits = System.Text.RegularExpressions.Regex.Replace(raw, "\\D", "");
        if (string.IsNullOrWhiteSpace(digits)) return digits;
        // preserve previous behavior: if length > 2, trim the last two digits (they were scanner suffix)
        if (digits.Length > 2)
            return digits.Substring(0, digits.Length - 2);
        return digits;
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
