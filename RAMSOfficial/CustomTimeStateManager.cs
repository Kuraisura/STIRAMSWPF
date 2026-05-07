using System;
using System.Collections.Generic;

namespace RAMSOfficial;

/// <summary>
/// Manages employee tap state during custom time testing
/// Prevents duplicate taps when changing custom time for testing
/// </summary>
public static class CustomTimeStateManager
{
    private static readonly Dictionary<int, EmployeeTapState> _employeeStates = new();

    public class EmployeeTapState
    {
        public int EmployeeId { get; set; }
        public DateTime LastTapTime { get; set; }
        public string LastTapType { get; set; } = string.Empty;
        public DateTime CustomTimeWhenTapped { get; set; }
        public bool HasTappedInToday { get; set; }
        public bool HasTappedOutToday { get; set; }
    }

    /// <summary>
    /// Record employee tap
    /// </summary>
    public static void RecordTap(int employeeId, string tapType, DateTime tapTime)
    {
        if (!_employeeStates.ContainsKey(employeeId))
        {
            _employeeStates[employeeId] = new EmployeeTapState
            {
                EmployeeId = employeeId
            };
        }

        var state = _employeeStates[employeeId];
        state.LastTapTime = tapTime;
        state.LastTapType = tapType;
        state.CustomTimeWhenTapped = TimeChangerWindow.GetCurrentTime();

        if (tapType == "IN")
        {
            state.HasTappedInToday = true;
        }
        else if (tapType == "OUT")
        {
            state.HasTappedOutToday = true;
        }

        System.Diagnostics.Debug.WriteLine($"?? State recorded for employee {employeeId}: {tapType} at {tapTime:HH:mm:ss}");
    }

    /// <summary>
    /// Check if employee can tap (prevent duplicates in testing)
    /// </summary>
    public static bool CanEmployeeTap(int employeeId, string tapType)
    {
        if (!TimeChangerWindow.IsTimeOverridden())
        {
            // Real time mode - always allow
            return true;
        }

        if (!_employeeStates.ContainsKey(employeeId))
        {
            // No previous taps - allow
            return true;
        }

        var state = _employeeStates[employeeId];
        var currentCustomTime = TimeChangerWindow.GetCurrentTime();

        // Check if trying to tap on same day
        if (state.CustomTimeWhenTapped.Date == currentCustomTime.Date)
        {
            // Same day - check if already tapped this type
            if (tapType == "IN" && state.HasTappedInToday)
            {
                System.Diagnostics.Debug.WriteLine($"?? Employee {employeeId} already tapped IN today (custom time testing)");
                return false;
            }

            if (tapType == "OUT" && state.HasTappedOutToday)
            {
                System.Diagnostics.Debug.WriteLine($"?? Employee {employeeId} already tapped OUT today (custom time testing)");
                return false;
            }
        }
        else
        {
            // Different day - reset daily flags
            state.HasTappedInToday = false;
            state.HasTappedOutToday = false;
        }

        return true;
    }

    /// <summary>
    /// Get employee state
    /// </summary>
    public static EmployeeTapState? GetEmployeeState(int employeeId)
    {
        return _employeeStates.ContainsKey(employeeId) ? _employeeStates[employeeId] : null;
    }

    /// <summary>
    /// Clear all state - called when custom time is changed or cleared
    /// </summary>
    public static void ClearState()
    {
        _employeeStates.Clear();
        System.Diagnostics.Debug.WriteLine("??? Employee tap state cleared");
    }

    /// <summary>
    /// Clear state for specific employee
    /// </summary>
    public static void ClearEmployeeState(int employeeId)
    {
        if (_employeeStates.ContainsKey(employeeId))
        {
            _employeeStates.Remove(employeeId);
            System.Diagnostics.Debug.WriteLine($"??? State cleared for employee {employeeId}");
        }
    }

    /// <summary>
    /// Get tap summary for debugging
    /// </summary>
    public static string GetStateSummary()
    {
        if (_employeeStates.Count == 0)
        {
            return "No employee taps recorded in custom time mode";
        }

        var summary = $"Employee Tap State ({_employeeStates.Count} employees):\n";
        foreach (var kvp in _employeeStates)
        {
            var state = kvp.Value;
            summary += $"  Employee {state.EmployeeId}: Last {state.LastTapType} at {state.LastTapTime:HH:mm:ss}\n";
            summary += $"    IN today: {state.HasTappedInToday}, OUT today: {state.HasTappedOutToday}\n";
        }

        return summary;
    }
}
