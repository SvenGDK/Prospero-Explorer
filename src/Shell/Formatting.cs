// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using System;

namespace ProsperoExplorer.Shell;

/// <summary>
/// Ways of writing a value out that are safe to reach from a module.
/// </summary>
/// <remarks>
/// A date must never be written with a custom format string here. Handing one to a date pulls in the
/// general formatter, which carries the time-zone specifiers, which reaches the local time zone, which
/// reads a time-zone database off the file system. None of that exists on this platform, and the cost
/// is not a failure at run time but a module that will not link at all: the database reader drags in
/// the process layer of the run-time support library, and thirteen of the names that layer needs are
/// published by nothing. The failure surfaces as a list of unresolved symbols with no obvious
/// connection to a date being printed.
///
/// Building the text from the parts avoids the whole chain, so that is what everything here does.
/// </remarks>
internal static class Formatting
{
    private static readonly string[] MonthAbbreviations =
    [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];

    /// <summary>A timestamp as year-month-day and the time, built from the parts.</summary>
    public static string Timestamp(DateTime when)
        => $"{when.Year:0000}-{Pad2(when.Month)}-{Pad2(when.Day)} {Pad2(when.Hour)}:{Pad2(when.Minute)}";

    /// <summary>A date alone, built from the parts.</summary>
    public static string Date(DateTime when) => $"{when.Year:0000}-{Pad2(when.Month)}-{Pad2(when.Day)}";

    /// <summary>A date with the month named, as a listing shows it.</summary>
    public static string ListingDate(DateTime when)
        => $"{MonthAbbreviations[when.Month - 1]} {when.Day,2} {Pad2(when.Hour)}:{Pad2(when.Minute)}";

    /// <summary>A two-digit number, left-padded with a nought.</summary>
    public static string Pad2(int value) => value < 10 ? "0" + value.ToString() : value.ToString();
}
