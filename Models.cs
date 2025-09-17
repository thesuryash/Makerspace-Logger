using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SpaceAccess.Wpf;

public class User
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public string StudentId { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Status { get; set; } = "active";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class Location
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public string Name { get; set; } = "";
    public int? Capacity { get; set; }
}

public class ScanEvent
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    [ForeignKey(nameof(UserId))] public User? User { get; set; }

    public string ScannedStudentId { get; set; } = "";
    public string NameAtScan { get; set; } = "";
    public string EventType { get; set; } = "entry"; // entry|exit|manual
    public Guid LocationId { get; set; }
    [ForeignKey(nameof(LocationId))] public Location? Location { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string RawPayloadJson { get; set; } = "{}";
}

// Lightweight view model for presenting events in the UI so we can keep the Id for deletes
public class ScanEventView
{
    public Guid Id { get; set; }
    public string TimestampLocal { get; set; } = "";
    public string EventType { get; set; } = "";
    public string ScannedStudentId { get; set; } = "";
    public string NameAtScan { get; set; } = "";
    public Guid? UserId { get; set; }
    public string LocationName { get; set; } = "";
}
