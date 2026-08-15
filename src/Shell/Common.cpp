#include "Common.h"

#include <shlobj_core.h>
#include <strsafe.h>
#include <cwchar>
#include <cstdarg>

namespace jalyro {

HINSTANCE g_hInst = nullptr;

// ---------------------------------------------------------------------------
// Paths
// ---------------------------------------------------------------------------

std::wstring ModulePath()
{
    wchar_t buf[MAX_PATH * 4] = {};
    DWORD n = ::GetModuleFileNameW(g_hInst, buf, ARRAYSIZE(buf));
    if (n == 0 || n >= ARRAYSIZE(buf))
        return std::wstring();
    return std::wstring(buf, n);
}

std::wstring ModuleDirectory()
{
    std::wstring p = ModulePath();
    size_t slash = p.find_last_of(L'\\');
    return (slash == std::wstring::npos) ? std::wstring() : p.substr(0, slash);
}

std::wstring UserProfile()
{
    wchar_t buf[MAX_PATH * 2] = {};
    DWORD n = ::GetEnvironmentVariableW(L"USERPROFILE", buf, ARRAYSIZE(buf));
    if (n == 0 || n >= ARRAYSIZE(buf))
        return std::wstring();
    return std::wstring(buf, n);
}

std::wstring StorageRoot()
{
    std::wstring profile = UserProfile();
    if (profile.empty())
        return std::wstring();

    std::wstring root = profile + L"\\.jalyro-convert";
    ::CreateDirectoryW(root.c_str(), nullptr);
    return root;
}

std::wstring SpoolDirectory()
{
    std::wstring root = StorageRoot();
    if (root.empty())
        return std::wstring();

    std::wstring dir = root + L"\\spool";
    ::CreateDirectoryW(dir.c_str(), nullptr);
    return dir;
}

std::wstring LogDirectory()
{
    std::wstring root = StorageRoot();
    if (root.empty())
        return std::wstring();

    std::wstring dir = root + L"\\logs";
    ::CreateDirectoryW(dir.c_str(), nullptr);
    return dir;
}

// ---------------------------------------------------------------------------
// Logging
// ---------------------------------------------------------------------------

// In a correct com:SurrogateServer registration this is "dllhost.exe".
// "explorer.exe" here means the isolation has been lost.
static std::wstring HostProcessName()
{
    wchar_t buf[MAX_PATH] = {};
    DWORD n = ::GetModuleFileNameW(nullptr, buf, ARRAYSIZE(buf));
    if (n == 0 || n >= ARRAYSIZE(buf))
        return L"<unknown>";

    std::wstring full(buf, n);
    size_t slash = full.find_last_of(L'\\');
    return (slash == std::wstring::npos) ? full : full.substr(slash + 1);
}

void Log(const wchar_t* format, ...)
{
    std::wstring dir = LogDirectory();
    if (dir.empty())
        return;

    std::wstring path = dir + L"\\shell.log";

    wchar_t body[2048] = {};
    va_list args;
    va_start(args, format);
    ::StringCchVPrintfW(body, ARRAYSIZE(body), format, args);
    va_end(args);

    SYSTEMTIME st = {};
    ::GetLocalTime(&st);

    wchar_t line[2600] = {};
    ::StringCchPrintfW(
        line, ARRAYSIZE(line),
        L"%04u-%02u-%02u %02u:%02u:%02u.%03u  host=%s pid=%lu tid=%lu  %s\r\n",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond,
        st.wMilliseconds,
        HostProcessName().c_str(),
        ::GetCurrentProcessId(), ::GetCurrentThreadId(), body);

    // Rotate past 2 MB, keeping one previous generation. Without this the log
    // grows for the life of the install - GetState is called on every
    // right-click.
    {
        WIN32_FILE_ATTRIBUTE_DATA info = {};
        if (::GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &info))
        {
            ULARGE_INTEGER size = {};
            size.HighPart = info.nFileSizeHigh;
            size.LowPart  = info.nFileSizeLow;
            if (size.QuadPart > 2ull * 1024 * 1024)
            {
                std::wstring previous = path + L".1";
                ::DeleteFileW(previous.c_str());
                ::MoveFileW(path.c_str(), previous.c_str());
            }
        }
    }

    HANDLE h = ::CreateFileW(path.c_str(), FILE_APPEND_DATA,
                             FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                             OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE)
        return;

    int bytes = ::WideCharToMultiByte(CP_UTF8, 0, line, -1, nullptr, 0, nullptr, nullptr);
    if (bytes > 1)
    {
        std::vector<char> utf8(static_cast<size_t>(bytes));
        ::WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8.data(), bytes, nullptr, nullptr);
        DWORD written = 0;
        ::WriteFile(h, utf8.data(), static_cast<DWORD>(bytes - 1), &written, nullptr);
    }
    ::CloseHandle(h);
}

// ---------------------------------------------------------------------------
// Selection
// ---------------------------------------------------------------------------

std::wstring ExtensionOf(const std::wstring& path)
{
    size_t dot   = path.find_last_of(L'.');
    size_t slash = path.find_last_of(L'\\');

    if (dot == std::wstring::npos)
        return std::wstring();
    if (slash != std::wstring::npos && dot < slash)
        return std::wstring();

    std::wstring ext = path.substr(dot);
    for (wchar_t& c : ext)
        c = static_cast<wchar_t>(::towlower(c));
    return ext;
}

// These three tables must stay in step with FormatTable.cs on the C# side.
static const wchar_t* kImageExt[] = {
    L".jpg", L".jpeg", L".png", L".webp", L".avif",
    L".heic", L".heif", L".bmp", L".tif", L".tiff", L".gif",
};
static const wchar_t* kVideoExt[] = {
    L".mp4", L".mov", L".mkv", L".webm", L".avi",
    L".m4v", L".wmv", L".flv", L".mpg", L".mpeg",
};
static const wchar_t* kAudioExt[] = {
    L".mp3", L".wav", L".flac", L".m4a", L".aac", L".ogg", L".opus", L".wma",
};

MediaKind KindOfExtension(const std::wstring& ext)
{
    for (const wchar_t* s : kImageExt) if (ext == s) return MediaKind::Image;
    for (const wchar_t* s : kVideoExt) if (ext == s) return MediaKind::Video;
    for (const wchar_t* s : kAudioExt) if (ext == s) return MediaKind::Audio;
    return MediaKind::Unsupported;
}

bool IsSupportedExtension(const std::wstring& ext)
{
    return KindOfExtension(ext) != MediaKind::Unsupported;
}

MediaKind KindOfSelection(IShellItemArray* items)
{
    std::vector<std::wstring> paths;
    if (FAILED(CollectPaths(items, paths)) || paths.empty())
        return MediaKind::Unsupported;

    MediaKind first = MediaKind::Unsupported;
    bool assigned = false;

    for (const std::wstring& p : paths)
    {
        MediaKind k = KindOfExtension(ExtensionOf(p));
        if (k == MediaKind::Unsupported)
            return MediaKind::Unsupported;   // one bad apple hides the menu

        if (!assigned)
        {
            first = k;
            assigned = true;
        }
        else if (k != first)
        {
            return MediaKind::Mixed;
        }
    }
    return first;
}

HRESULT CollectPaths(IShellItemArray* items, std::vector<std::wstring>& out)
{
    out.clear();
    if (!items)
        return E_INVALIDARG;

    DWORD count = 0;
    HRESULT hr = items->GetCount(&count);
    if (FAILED(hr))
        return hr;

    if (count > kMaxSelection)
        count = kMaxSelection;

    out.reserve(count);

    for (DWORD i = 0; i < count; ++i)
    {
        IShellItem* item = nullptr;
        if (FAILED(items->GetItemAt(i, &item)) || !item)
            continue;

        PWSTR path = nullptr;
        // SIGDN_FILESYSPATH fails for virtual items - that is the filter.
        if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path)
        {
            out.emplace_back(path);
            ::CoTaskMemFree(path);
        }
        item->Release();
    }
    return S_OK;
}

bool SelectionIsSupported(IShellItemArray* items)
{
    MediaKind k = KindOfSelection(items);
    return k != MediaKind::Unsupported;
}

// ---------------------------------------------------------------------------
// Handoff
// ---------------------------------------------------------------------------

static void AppendUtf8(HANDLE h, const std::wstring& text)
{
    int bytes = ::WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1,
                                      nullptr, 0, nullptr, nullptr);
    if (bytes <= 1)
        return;

    std::vector<char> utf8(static_cast<size_t>(bytes));
    ::WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1,
                          utf8.data(), bytes, nullptr, nullptr);
    DWORD written = 0;
    ::WriteFile(h, utf8.data(), static_cast<DWORD>(bytes - 1), &written, nullptr);
}

HRESULT SignalHost(const std::wstring& message)
{
    // No WaitNamedPipe: if nothing is listening, CreateFile fails immediately
    // with ERROR_FILE_NOT_FOUND. Blocking here would stall Explorer's UI thread.
    HANDLE pipe = ::CreateFileW(kPipeName, GENERIC_WRITE, 0, nullptr,
                                OPEN_EXISTING, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE)
        return HRESULT_FROM_WIN32(::GetLastError());

    int bytes = ::WideCharToMultiByte(CP_UTF8, 0, message.c_str(), -1,
                                      nullptr, 0, nullptr, nullptr);
    if (bytes <= 1)
    {
        ::CloseHandle(pipe);
        return E_FAIL;
    }

    std::vector<char> utf8(static_cast<size_t>(bytes));
    ::WideCharToMultiByte(CP_UTF8, 0, message.c_str(), -1,
                          utf8.data(), bytes, nullptr, nullptr);

    DWORD written = 0;
    BOOL ok = ::WriteFile(pipe, utf8.data(),
                          static_cast<DWORD>(bytes - 1), &written, nullptr);
    ::CloseHandle(pipe);

    return ok ? S_OK : HRESULT_FROM_WIN32(::GetLastError());
}

// Cold start only. The spawned Host WILL inherit package identity; it detects
// that and relaunches itself clean. See Program.cs.
static void SpawnHostFallback()
{
    std::wstring exe = ModuleDirectory() + L"\\Jalyro.Convert.Host.exe";
    std::wstring cmd = L"\"" + exe + L"\" --resident";

    std::vector<wchar_t> mutableCmd(cmd.begin(), cmd.end());
    mutableCmd.push_back(L'\0');

    STARTUPINFOW si = {};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi = {};

    if (::CreateProcessW(exe.c_str(), mutableCmd.data(), nullptr, nullptr, FALSE,
                         CREATE_UNICODE_ENVIRONMENT, nullptr, nullptr, &si, &pi))
    {
        ::CloseHandle(pi.hThread);
        ::CloseHandle(pi.hProcess);
        Log(L"  cold start: spawned Host (it will relaunch itself clean)");
    }
    else
    {
        Log(L"  cold start: CreateProcess failed err=%lu", ::GetLastError());
    }
}

HRESULT DispatchJob(IShellItemArray* items, const wchar_t* verb)
{
    LARGE_INTEGER freq = {}, start = {}, stop = {};
    ::QueryPerformanceFrequency(&freq);
    ::QueryPerformanceCounter(&start);

    std::vector<std::wstring> paths;
    HRESULT hr = CollectPaths(items, paths);
    if (FAILED(hr))
    {
        Log(L"Invoke(%s): CollectPaths failed hr=0x%08X", verb, hr);
        return hr;
    }

    std::wstring spool = SpoolDirectory();
    if (spool.empty())
    {
        Log(L"Invoke(%s): no spool directory", verb);
        return E_FAIL;
    }

    // Write to .tmp then rename to .job, so the Host's directory watcher can
    // never observe a half-written file.
    // A GUID, not tick count plus PID: two right-clicks in the same
    // millisecond from the same surrogate would collide, and one job would
    // silently overwrite the other.
    GUID guid = {};
    ::CoCreateGuid(&guid);

    wchar_t stem[128] = {};
    ::StringCchPrintfW(stem, ARRAYSIZE(stem),
        L"\\job-%08lX%04hX%04hX%02X%02X%02X%02X%02X%02X%02X%02X",
        guid.Data1, guid.Data2, guid.Data3,
        guid.Data4[0], guid.Data4[1], guid.Data4[2], guid.Data4[3],
        guid.Data4[4], guid.Data4[5], guid.Data4[6], guid.Data4[7]);

    std::wstring tmpPath = spool + stem + L".tmp";
    std::wstring jobPath = spool + stem + L".job";

    HANDLE h = ::CreateFileW(tmpPath.c_str(), GENERIC_WRITE, FILE_SHARE_READ,
                             nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE)
    {
        Log(L"Invoke(%s): cannot create job file (err=%lu)", verb, ::GetLastError());
        return HRESULT_FROM_WIN32(::GetLastError());
    }

    AppendUtf8(h, std::wstring(L"verb=") + verb + L"\r\n");
    {
        wchar_t countLine[64] = {};
        ::StringCchPrintfW(countLine, ARRAYSIZE(countLine), L"count=%zu\r\n", paths.size());
        AppendUtf8(h, countLine);
    }
    for (const std::wstring& p : paths)
        AppendUtf8(h, p + L"\r\n");
    ::CloseHandle(h);

    if (!::MoveFileExW(tmpPath.c_str(), jobPath.c_str(), MOVEFILE_REPLACE_EXISTING))
    {
        Log(L"Invoke(%s): rename to .job failed err=%lu", verb, ::GetLastError());
        ::DeleteFileW(tmpPath.c_str());
        return HRESULT_FROM_WIN32(::GetLastError());
    }

    // Try the resident Host. This is the question v0.2.1 is really asking:
    // does NPFS cross the package identity boundary?
    HRESULT signalled = SignalHost(jobPath);
    const wchar_t* route = L"spooled";

    if (SUCCEEDED(signalled))
    {
        route = L"pipe";
    }
    else
    {
        Log(L"  pipe not available (hr=0x%08X) - job left in spool", signalled);
        SpawnHostFallback();
        route = L"spawn";
    }

    ::QueryPerformanceCounter(&stop);
    double ms = (freq.QuadPart > 0)
        ? (double)(stop.QuadPart - start.QuadPart) * 1000.0 / (double)freq.QuadPart
        : -1.0;

    Log(L"Invoke(%s): %zu item(s), route=%s, spool=%s, elapsed=%.2f ms  [target < 50 ms]",
        verb, paths.size(), route, spool.c_str(), ms);

    return S_OK;
}

} // namespace jalyro
