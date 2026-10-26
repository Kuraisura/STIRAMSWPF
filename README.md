# STIRAMS WPF

A Windows RFID kiosk that records employee attendance locally and synchronizes it with the STIRAMS dashboard.

**Windows** · **Institutional Project** · 2026

[Portfolio](https://kuraisler.xyz)

## Purpose

STIRAMS WPF captures the attendance events that the web dashboard manages. Employees tap an RFID card at a Windows terminal, receive immediate feedback, and have their attendance checked against schedules and related rules. Local storage allows the kiosk to retain records when its database connection is unavailable.

## Intended users

Faculty and employees clocking in or out, plus campus staff operating the attendance terminals at STI College Santa Rosa.

## Main capabilities

- Read RFID card input and show the employee profile and attendance result.
- Validate taps against schedules, holidays, leave, and substitution rules.
- Store attendance locally and synchronize pending records in the background.
- Provide kiosk screens, reader detection, and diagnostic tools for operators.

## How it was built

### Windows client

C# and WPF on .NET 8 implement the kiosk screens. View models and dedicated services separate display logic from attendance processing.

### Offline data flow

SQLite holds local records and cached schedules. Background synchronization sends pending attendance to the shared PostgreSQL database.

### Reader and database integration

RFID input and Windows device detection connect the terminal to readers. Dapper and Npgsql handle database access; Material Design components style the interface.

## Technology stack

| Responsibility | Technologies |
| --- | --- |
| Windows application | C#, .NET 8, WPF, CommunityToolkit.Mvvm |
| Storage & sync | SQLite, PostgreSQL, Dapper, Npgsql |
| Interface & hardware | MaterialDesignThemes, WMI, USB RFID input |

## Project scope and current state

The desktop component of the STIRAMS institutional project. It requires Windows, a compatible RFID reader, and the configured shared database. Offline capture depends on the local employee and schedule cache.

## Source guide

- [`RAMSOfficial/Services/`](RAMSOfficial/Services/)
- [`RAMSOfficial/ViewModels/`](RAMSOfficial/ViewModels/)
- [`RAMSOfficial/TapInWindow.xaml.cs`](RAMSOfficial/TapInWindow.xaml.cs)

## Development setup

Requires Windows, the .NET 8 SDK, a compatible RFID reader, a local SQLite cache, and the configured shared PostgreSQL database. Review application settings and database services for your development environment. Open the project in Visual Studio to run or debug the kiosk.

```sh
dotnet restore RAMSOfficial/RAMSOfficial.csproj
dotnet build RAMSOfficial/RAMSOfficial.csproj
```

## Screenshot and module walkthrough

Expand a screen below to see the capture and an explanation of the module, its visible controls, and its role in the application.

<details open>
<summary><strong>01 · RFID kiosk ready state</strong></summary>

The terminal displays Ready to Scan, a manual-entry shortcut, and a recent-attendance panel. This is the waiting state before an employee presents a registered RFID card.

<p><img src="screenshots/1.png" alt="RFID kiosk ready state" width="960"></p>

</details>

<details>
<summary><strong>02 · Employment-start validation</strong></summary>

A warning explains that the selected employee has not reached the scheduled employment start date. It shows how the attendance validator blocks a tap that falls before the permitted start date.

<p><img src="screenshots/2.png" alt="Employment-start validation" width="960"></p>

</details>

<details>
<summary><strong>03 · Controlled date/time test tool</strong></summary>

The Time Changer dialog lets a tester select a date/time, preview it, apply an override, or clear it. It supports controlled attendance scenarios without presenting the override as a normal employee operation.

<p><img src="screenshots/3.png" alt="Controlled date/time test tool" width="960"></p>

</details>

<details>
<summary><strong>04 · Time-in confirmation</strong></summary>

The feedback screen identifies the employee, recorded time, department, role, and attendance status. The captured test displays an on-time result, giving the person immediate confirmation that the tap was recorded.

<p><img src="screenshots/4.png" alt="Time-in confirmation" width="960"></p>

</details>

<details>
<summary><strong>05 · Time-out confirmation</strong></summary>

The corresponding time-out screen shows the employee and the end-of-day tap result. It completes the visible attendance pair while keeping recorded time and status readable at the kiosk.

<p><img src="screenshots/5.png" alt="Time-out confirmation" width="960"></p>

</details>

---

Project by [Mark Crysler Baddo](https://github.com/Kuraisura) · [Portfolio](https://kuraisler.xyz)
