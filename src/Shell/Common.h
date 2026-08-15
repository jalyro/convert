// Common.h - shared helpers for the Jalyro Convert shell extension.
//
// v0.2.1 architecture change: SIGNAL, DON'T SPAWN.
//
// Phase 1 established that a child process launched from the COM surrogate
// inherits package identity, and that PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY
// does not break it out (both documented values tested, both failed). So the
// shell extension no longer launches the Host in the normal case. It drops a
// job file into a spool directory and pokes a resident Host over a named pipe.
// Nothing crosses the identity boundary, so the boundary stops mattering.
//
// Constraints unchanged: these run on Explorer's UI thread. No file is opened
// for inspection, no network access, and Invoke must return well under 50 ms.

#pragma once

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include <windows.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <string>
#include <vector>

namespace jalyro {

// Must match AppxManifest.xml exactly (com:Class Id and desktop5:Verb Clsid).
constexpr wchar_t kClsidString[] = L"{EF3A7DDF-F221-42D9-9227-EB522E46F971}";

// The pipe the resident Host listens on. Named pipes live in NPFS, a different
// namespace from the BaseNamedObjects one we proved is shared - so whether
// this crosses the package boundary is an open question v0.2.1 exists to
// answer. The spool directory is the fallback either way.
constexpr wchar_t kPipeName[] = L"\\\\.\\pipe\\Jalyro.Convert.Host";

extern HINSTANCE g_hInst;

// ---------------------------------------------------------------------------
// Paths
// ---------------------------------------------------------------------------

// Full path of this DLL. Not the host process - in a surrogate that is
// dllhost.exe.
std::wstring ModulePath();
std::wstring ModuleDirectory();

// %USERPROFILE%, from the environment.
std::wstring UserProfile();

// Storage root: %USERPROFILE%\.jalyro-convert
//
// Deliberately OUTSIDE AppData. MSIX filesystem virtualization targets
// AppData\Local, AppData\Roaming and ProgramData; the profile root appears not
// to be redirected. "Appears" is doing work in that sentence - v0.2.1 reports
// exactly where things land so it can be checked rather than believed.
std::wstring StorageRoot();

// <StorageRoot>\spool - job files waiting to be picked up.
std::wstring SpoolDirectory();

// <StorageRoot>\logs - shell.log lives here.
std::wstring LogDirectory();

// ---------------------------------------------------------------------------
// Logging
// ---------------------------------------------------------------------------

// Appends a timestamped UTF-8 line carrying the hosting process name, so the
// dllhost-vs-explorer question is answerable straight from the log.
// Best effort; never throws, never blocks on failure.
void Log(const wchar_t* format, ...);

// ---------------------------------------------------------------------------
// Selection
// ---------------------------------------------------------------------------

// What the selection is, which decides what the flyout offers.
enum class MediaKind { Unsupported, Image, Video, Audio, Mixed };

std::wstring ExtensionOf(const std::wstring& path);
bool IsSupportedExtension(const std::wstring& ext);
MediaKind KindOfExtension(const std::wstring& ext);

// Single kind across the whole selection, or Mixed / Unsupported.
MediaKind KindOfSelection(IShellItemArray* items);

constexpr unsigned kMaxSelection = 5000;
HRESULT CollectPaths(IShellItemArray* items, std::vector<std::wstring>& out);
bool SelectionIsSupported(IShellItemArray* items);

// ---------------------------------------------------------------------------
// Handoff
// ---------------------------------------------------------------------------

// Writes text to the pipe. S_OK if a listener accepted it.
HRESULT SignalHost(const std::wstring& message);

// Writes the job file to the spool directory, then tries to signal a resident
// Host. Three outcomes, all logged:
//   pipe    - a resident Host was signalled. Fast, correct path.
//   spooled - no Host listening; the job waits in the spool. The Host drains
//             pending jobs on startup, so nothing is lost.
//   spawn   - no Host listening and cold-start fallback enabled. Launches the
//             Host, which inherits package identity and relaunches itself
//             clean. Slower; only on first run after install.
HRESULT DispatchJob(IShellItemArray* items, const wchar_t* verb);

} // namespace jalyro
