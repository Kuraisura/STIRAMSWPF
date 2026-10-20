# 🏫 STIRAMS (WPF)

**RFID Attendance Management System — Desktop** · `Windows Forms`

Desktop RFID attendance management system for STI College Santa Rosa with time-in/out recording, employee scheduling, and DTR tracking.

## Why This Project

Before STIRAMS, faculty at STI College Santa Rosa clocked in with pen and paper: a log sheet by the door, handwritten times, and an HR clerk who transcribed every row into the Daily Time Record used for payroll. Sheets got lost, times got guessed, and arguments over "was I late or not?" had no evidence to settle them. This Windows kiosk is the front line of the fix — placed at the campus entrance, it turns every RFID tap into a timestamped, validated record with an immediate ON TIME / LATE verdict, so payroll starts from facts instead of footnotes.

Built as internship/capstone work alongside the STIRAMS web dashboard, the kiosk is designed for unattended, all-day operation: it locks the screen into kiosk mode, recovers automatically if the reader unplugs, keeps working through internet outages by caching taps locally, and enforces the same schedule, holiday, leave, and substitution rules the web side manages — so a tap means the same thing at 7:00 AM at the door as it does during cutoff review in HR.

## How It Works

1. Staff boots the kiosk in the morning; the "waiting for reader" screen is shown until the RFID reader is detected, then the main scanner window takes over full-screen with a focus guard that keeps the input locked on the card field.
2. A professor taps her card; the reader's keyboard-wedge input is collected into a UID buffer within a 300 ms window, the card is looked up in the local cache, and her profile photo, department, and today's logs are rendered on the confirmation card.
3. The validation engine resolves the tap against the day's rules in priority order — holiday/suspension, leave/verification, substitution, exam schedule, class schedule, then regular schedule — and classifies the result as ON TIME or LATE (no grace period; lateness is recorded to the minute).
4. A success chime plays, the card displays who tapped, when, and the status, then auto-hides after 10 seconds and wipes the sensitive data from the screen. When both time-in and time-out exist, a "complete attendance" card confirms the pair.
5. The log is saved to the local SQLite store first, then pushed to the central STIRAMS database when the network is up; a 30-second background sync drains anything left over, so no tap is ever lost during an outage.
6. At 5:00 PM, an admin opens the web dashboard and sees the day's taps, irregularities, and lates — ready for DTR generation — while any schedule, substitution, or holiday they change is picked up by the kiosk's cache on its next refresh.

## Modules

### RFID Kiosk & Reader Detection
The always-on scanner shell: `RfidScannerWindow`/`WaitingForReaderWindow` show a "READY TO SCAN" state, `SimpleRfidDetectionService` and `RfidReaderDetectionService` watch for the reader to come online, and a 250 ms focus-guard timer keeps the hidden input field locked no matter what steals focus.
**Scenario:** The guard restarts the kiosk PC after lunch; the app opens to the waiting screen, detects the USB reader moments later, and switches to the live scanner — the next professor who taps never sees a mouse cursor or a stray window.

### Tap Confirmation & Profile Display
`TapInWindow`, `CompleteAttendanceWindow`, and `EmployeeProfileWindow` render the tap result: employee photo, name, ID, department, role, exact tap time, today's logs, and ON TIME / LATE status, animated in with GPU-composited storyboards, a sound chime, a 10-second countdown, and an automatic wipe of personal data afterwards.
**Scenario:** Sir Reyes taps at 7:58 AM for an 8:00 AM class; his photo card slides in with "ON TIME" and a green chime, stays readable for ten seconds while the line moves, then clears itself so the next person's data is never shown to him.

### Attendance Validation & Status Engine
`AttendanceValidationService` resolves each tap through a strict priority order — holiday/suspension (1), leave/missed-log/verification (2), substitutions (3), exam schedules (4), class schedules (5), regular schedule (6) — and `StatusCalculationHelper` computes the verdict with minute precision: no grace period on time-in, and early departures flagged as undertime.
**Scenario:** An instructor with an approved field trip taps at 9:00 AM though her class starts at 7:00; the engine sees the leave at priority 2 before it ever reaches the class schedule, so she is recorded as on leave instead of being falsely marked late.

### Schedule Enforcement & Caching
`EmployeeScheduleService`, `ScheduleCacheService`, `DynamicScheduleUpdater`, and `ScheduleConflictHelper` keep the kiosk's view of class loads, exam duties, and start dates current, while start-date validation blocks taps before a new hire's contracted employment begins.
**Scenario:** A newly hired instructor tries tapping her card the day before her official start date; the kiosk politely refuses with a schedule-enforcement message, protecting payroll from paying a day that was never contracted.

### Substitution-Aware Taps
`SubstitutionScheduleHelper`, `SubstitutionValidationService`, and `AllSchedulesSubstitutedHelper` make the kiosk honor assignments made in the web dashboard, so a substitute's tap validates against the class they are covering and the original teacher's absence is accounted for.
**Scenario:** The admin assigns Ma'am Flores to cover Sir Gomez's 8:00 AM section from her desk; when Ma'am Flores taps at 7:55, the kiosk validates her against that substituted class and records her as the teacher of record for the period.

### Hybrid Offline Sync
`HybridDatabaseService`, `LocalDatabaseService`, `HybridAttendanceSyncService`, `PendingAttendanceXmlMirrorService`, and `NetworkStatusService` implement a save-first pipeline: every tap lands in a local SQLite store immediately, a 30-second background timer pushes unsynced records to the central database, and an XML mirror keeps a pending record even if the app is closed mid-outage.
**Scenario:** The campus internet drops during a storm; taps keep recording locally all morning, and as soon as connectivity returns the sync engine drains the queue — HR's records for the day look identical to a day with perfect uptime.

### Time Override QA Tool
Password-protected `TimeChangerWindow` replaces `DateTime.Now` application-wide with a simulated clock that still advances in real time, so testers can exercise midnight rollovers, holidays, and shift edge cases without waiting for the real clock.
**Scenario:** A tester sets the kiosk clock to 11:58 PM to verify a night-shift time-out; the override advances naturally past midnight, proving the day-boundary logic before the system ever meets it in production.

### Diagnostics & Developer Console
`DeveloperConsoleWindow` (with its own auth gate), `DatabaseExplorerWindow`, `DatabaseDiagnostics`, `DiagnosticLogger`, and `RfidAttemptLoggingService` expose connection health, schema inspection, and every raw RFID attempt for troubleshooting on-site.
**Scenario:** A card won't register at the entrance; the IT officer opens the developer console, checks the attempt log, and sees the reader is emitting truncated UIDs — he reseats the cable and confirms successful reads without uninstalling anything.

### Manual Entry & Attendance Repair
`ManualIdInputDialog` and `CompleteAttendanceWindow` handle the human fallbacks: keying an ID when a card is unreadable, and confirming a finished time-in/time-out pair with the day's full log on screen.
**Scenario:** A professor's card is cracked and won't read; the staff member keys her employee ID manually, the same validation engine runs, and her day closes with the complete-attendance card showing both her time-in and time-out.

## Features

- RFID card tap time-in/time-out with visual confirmation
- ON TIME / LATE status classification
- Employee schedule enforcement with start-date validation
- Real-time attendance log display
- Time override testing tool for QA
- Employee profile integration (name, ID, department, role)

## Tech Stack

- C#
- WPF
- .NET
- SQL Server
- RFID SDK

## About

Windows desktop application that interfaces with RFID card readers to record employee attendance. Built with WPF for a modern UI, connecting to SQL Server for persistent storage. Includes validation logic for schedule enforcement and a testing tool for time simulation.

## Screenshots

### Idle scanner state — the kiosk screen employees see when approaching the RFID reader. "READY TO SCAN" confirms the system is online and waiting for a card tap. The recent panel fills up as people clock in.

![Idle scanner state — the kiosk screen employees see when approaching the RFID reader. "READY TO SCAN" confirms the system is online and waiting for a card tap. The recent panel fills up as people clock in.](screenshots/1.png)

### Schedule enforcement — prevents employees from clocking in before their official start date. This validation ensures new hires can't log attendance before their contracted employment begins, protecting payroll accuracy.

![Schedule enforcement — prevents employees from clocking in before their official start date. This validation ensures new hires can't log attendance before their contracted employment begins, protecting payroll accuracy.](screenshots/2.png)

### QA testing tool — allows developers and testers to simulate different dates and times without waiting for the real clock. Essential for testing edge cases like midnight shifts, holidays, and DST changes.

![QA testing tool — allows developers and testers to simulate different dates and times without waiting for the real clock. Essential for testing edge cases like midnight shifts, holidays, and DST changes.](screenshots/3.png)

### Time-in confirmation — provides immediate visual feedback when an employee taps their card: who tapped, their department, the exact time, and whether they are ON TIME or LATE. Eliminates ambiguity about whether the tap registered.

![Time-in confirmation — provides immediate visual feedback when an employee taps their card: who tapped, their department, the exact time, and whether they are ON TIME or LATE. Eliminates ambiguity about whether the tap registered.](screenshots/4.png)

### Time-out confirmation — mirrors the time-in screen but records the end-of-day tap, completing the attendance pair. The ON TIME status here indicates the employee worked their full required shift.

![Time-out confirmation — mirrors the time-in screen but records the end-of-day tap, completing the attendance pair. The ON TIME status here indicates the employee worked their full required shift.](screenshots/5.png)

---

Built by [Mark Crysler Baddo](https://github.com/Kuraisura) · [Portfolio](https://kuraisler.xyz)
