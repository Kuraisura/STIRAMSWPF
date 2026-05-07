# 🏫 STIRAMS (WPF)

**RFID Attendance Management System — Desktop** · `Windows Forms`

Desktop RFID attendance management system for STI College Santa Rosa with time-in/out recording, employee scheduling, and DTR tracking.

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

---

Built by [Mark Crysler Baddo](https://github.com/Kuraisura) · [Portfolio](https://kuraisler.xyz)
