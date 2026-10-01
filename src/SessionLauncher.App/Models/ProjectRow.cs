// ProjectRow.cs
//
// One row of the 项目 list: a project with its colour and its session count.
//
// Deliberately built on ProjectCatalog.ProjectEntry rather than the log-derived
// ProjectInfo: a PROJECT is "a set of sessions that share a directory", which is what
// ProjectCatalog groups. ProjectInfo came from opencode's logs and is a different
// thing entirely (every directory opencode ever touched, ~135 of them, mostly
// dead). OpenChamber's own 项目 panel lists the handful the user actually added.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using SessionLauncher.App.Services;

namespace SessionLauncher.App.Models;

/// <summary>One project, ready to display.</summary>
public sealed class ProjectRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public ProjectRow(ProjectEntry entry)
    {
        Entry = entry;
    }

    /// <summary>The grouped project this row renders.</summary>
    public ProjectEntry Entry { get; }

    public string Path => Entry.Path;

    public string Name => Entry.Label;

    /// <summary>Hex colour of the folder icon, from OpenChamber's palette.</summary>
    public string ColorHex => Entry.ColorHex;

    /// <summary>Colour name, so the choice is visible and not just a swatch.</summary>
    public string ColorLabel =>
        ProjectCatalog.Colors.FirstOrDefault(c => c.Key == Entry.ColorKey)?.Label ?? "";

    public int SessionCount => Entry.SessionCount;

    public string LastSeenText => Entry.LastUsed == DateTimeOffset.MinValue
        ? "—"
        : Entry.LastUsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Session ids, newest first. This is the order the tabs get opened in.</summary>
    public IReadOnlyList<string> SessionIds => Entry.Sessions.Select(s => s.Id).ToList();

    /// <summary>Session titles, newest first, for the status line and tooltips.</summary>
    public IReadOnlyList<string> SessionTitles => Entry.Sessions.Select(s => s.DisplayTitle).ToList();

    public bool DirectoryExists => Entry.DirectoryExists;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
