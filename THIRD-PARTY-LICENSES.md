# Third-party licenses

Net-Peekier bundles the following third-party components.

## LibreHardwareMonitorLib

Used for hardware sensor readings (CPU/GPU/RAM temperatures, GPU load/clock)
in the System Stats window.

- Project: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- License: Mozilla Public License 2.0 (MPL-2.0)

The MPL-2.0 requires that a copy of the license accompany distributions of
the covered software. The full license text is available at:

  https://www.mozilla.org/en-US/MPL/2.0/

LibreHardwareMonitorLib is used unmodified, referenced as a NuGet package.
MPL-2.0 is a file-level copyleft license: it permits use in larger works
(including closed-source ones) provided the MPL-covered files themselves are
not modified, or — if modified — their source is made available. We do not
modify them.

## Microsoft.Diagnostics.Tracing.TraceEvent

Used for ETW per-process byte counting.

- Project: https://github.com/microsoft/perfview
- License: MIT

## Other Microsoft packages

System.Diagnostics.PerformanceCounter and Microsoft.Win32.Registry are
distributed by Microsoft under the MIT license.
