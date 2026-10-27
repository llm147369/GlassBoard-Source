# Third-party component

Upstream base project: GlassBoard is a heavily modified fork of an
open-source desktop launcher.

NPSMLib by ADeltaX, MIT License, copyright 2021 ADeltaX.
Source: https://github.com/ADeltaX/NPSMLib
Pinned commit: 22616b82f9b6ffd43ecf863f89455766edf63c76
Included: lib/NPSMLib.dll and the full license at licenses/NPSMLib_LICENSE.txt.
The upstream source is not vendored into this repository; use the pinned
commit above. The upstream source is unmodified. Built against .NET Framework 4.8
reference assemblies with Roslyn (.NET SDK 8.0.408), AnyCPU.
One upstream warning: unused NPSMEventRegistrationToken.value (CS0169).
To rebuild with an installed .NET SDK: clone the upstream repo at the pinned
commit and run dotnet build NPSMLib.csproj -f net45 -c Release; copy the
resulting NPSMLib.dll into lib/. The bundled library is loaded at runtime;
no additional runtime installation.

Integration: src/MediaBridge.ps1 loads this library in its isolated helper
process. GSMTC remains the primary API. NPSM supplies matching-session
artwork and timeline/seek support when available. Session matching checks
application identity and current track before using a fallback. Unsupported
private API initialization is caught and GSMTC remains usable.
This does not manufacture missing song artwork or timeline data, and does
not access music accounts or online cover-search services.

Other reviewed projects (no source copied):
https://github.com/DubyaDude/WindowsMediaController
https://github.com/ModernFlyouts-Community/ModernFlyouts

Bundled COM music adapter: Widdit/now-playing-service (MIT), commit
2336c5fc6c57300bc7cf5fa85c7f273042bb86b1. Upstream NeteaseMusicHelper source
is now directly compiled with documented local patches. Its upstream
Interop.UIAutomationClient.dll is bundled beside GlassBoard.exe.
See vendor/now-playing-service/REFERENCE.md and LICENSE.

Weather backup in v0.9.9: https://wttr.in/ (https://github.com/chubin/wttr.in). Requested only when the Open-Meteo lookup/forecast fails. Requests contain the entered city or resolved coordinates; no account integration. Actual returned provider is displayed. No wttr.in source code is bundled.
