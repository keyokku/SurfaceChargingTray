Surface Charging Tray — Diagnostic Tool
========================================

What this is
------------
A small standalone tool that captures information about your Surface app's
UI so the developer can support your device. Nothing is sent automatically:
the output is saved as a file in this folder, and you manually attach it
to a new GitHub issue.

How to use
----------
1. Pick the .exe matching your CPU:

     SurfaceChargingTrayDiagnostic-x64.exe     for Intel-based Surfaces
                                               (most common)

     SurfaceChargingTrayDiagnostic-arm64.exe   for Snapdragon-based
                                               Surfaces (Pro X, Pro 11/12,
                                               Laptop 7 Snapdragon)

   Find your CPU at:  Settings -> System -> About -> System type

2. Double-click the right .exe. A small dialog will appear.

3. Open the Microsoft Surface app and navigate to the page that's failing
   (typically Battery & charging).

4. Click "Run Test" in the dialog. The scan takes ~15-30 seconds.

5. Two files will be saved in this folder:

     surface-diagnostic-YYYY-MM-DD_HHMM.txt
     surface-app-YYYY-MM-DD_HHMM.png

6. Post a comment on the diagnostic-results thread and attach BOTH files:

     https://github.com/keyokku/SurfaceChargingTray/issues/2

   Include a sentence about what you were doing when the main app failed
   (e.g. "tried to set charging mode to 80% from the tray menu, got the
   'card not found' error").

Requirements
------------
- Windows 10 build 19041 (May 2020) or newer; Windows 11 recommended
- .NET 8 Desktop Runtime — Windows will offer a download link on first
  launch if you don't have it. (If you already have the main Surface
  Charging Tray running, you already have .NET 8.)

What gets captured
------------------
- Your Windows version, locale, device model, .NET version
- The Surface app's package family name and version
- The full UIA (UI Automation) element tree of the Surface app's currently-
  visible page (up to 1500 elements, unlimited depth)
- A screenshot of the Surface app's window (window only — not your full
  screen)
- The contents of `settings.ini` if you placed this tool in the same
  folder as the main Surface Charging Tray .exe

What does NOT get captured
--------------------------
- Anything outside the Surface app's window
- Your files, browser history, environment variables, credentials, etc.
- Network requests — the tool doesn't make any. No uploads.

You can open the .txt file in Notepad before submitting to see exactly
what's there.

License: MIT. See the main repo for the LICENSE file.
