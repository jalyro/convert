// ExplorerCommand.cpp - the whole Jalyro Convert shell extension.
//
// Deliberate constraints, all of which the real product must also honour:
//
//   * No file is ever opened. GetState decides from the extension string only.
//   * No network access from any method (Microsoft's docs are explicit: these
//     run on Explorer's UI thread).
//   * Invoke serialises the selection and CreateProcess()es, then returns.
//     It never waits, never creates a window, never pumps messages - the
//     surrogate host is torn down within seconds of Invoke returning.
//   * Exactly ONE level of submenu. Explorer does not support subcommands
//     that themselves have subcommands.

#include "Common.h"

#include <wrl/implements.h>
#include <wrl/client.h>
#include <new>
#include <mutex>

using namespace Microsoft::WRL;

namespace {

// ===========================================================================
// A single leaf command in the "Convert to" flyout.
// ===========================================================================
class SubCommand
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand>
{
public:
    HRESULT RuntimeClassInitialize(PCWSTR title, PCWSTR verb, EXPCMDFLAGS flags)
    {
        m_title = title;
        m_verb  = verb;
        m_flags = flags;
        return S_OK;
    }

    // -- IExplorerCommand ---------------------------------------------------

    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* ppszName) override
    {
        if (m_flags & ECF_ISSEPARATOR)
        {
            *ppszName = nullptr;
            return E_NOTIMPL;
        }
        return ::SHStrDupW(m_title.c_str(), ppszName);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* ppszIcon) override
    {
        *ppszIcon = nullptr;
        return E_NOTIMPL;   // leaf items inherit the parent's look
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* ppszInfotip) override
    {
        *ppszInfotip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* pguidCommandName) override
    {
        *pguidCommandName = GUID_NULL;
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* pCmdState) override
    {
        *pCmdState = ECS_ENABLED;
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* pFlags) override
    {
        *pFlags = m_flags;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** ppEnum) override
    {
        // Two levels of nesting are NOT supported by Explorer. Never return
        // an enumerator from a leaf.
        *ppEnum = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* psiItemArray, IBindCtx*) override
    {
        // ---- Checklist item: deliberate crash, to prove Explorer survives.
        if (m_verb == L"crashtest")
        {
            jalyro::Log(L"Invoke(crashtest): deliberately faulting this process now. "
                     L"Explorer must remain alive.");

            // Force an access violation. If the surrogate registration is
            // correct this kills dllhost.exe only.
            volatile int* boom = nullptr;
            *boom = 1;
            return E_FAIL;   // not reached
        }

        return jalyro::DispatchJob(psiItemArray, m_verb.c_str());
    }

private:
    std::wstring m_title;
    std::wstring m_verb;
    EXPCMDFLAGS  m_flags = ECF_DEFAULT;
};

// ===========================================================================
// Enumerator handed back from the root command's EnumSubCommands.
// ===========================================================================
class SubCommandEnum
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IEnumExplorerCommand>
{
public:
    HRESULT RuntimeClassInitialize(std::vector<ComPtr<IExplorerCommand>>&& commands)
    {
        m_commands = std::move(commands);
        m_index = 0;
        return S_OK;
    }

    IFACEMETHODIMP Next(ULONG celt,
                        IExplorerCommand** pUICommand,
                        ULONG* pceltFetched) override
    {
        ULONG fetched = 0;
        for (ULONG i = 0; i < celt && m_index < m_commands.size(); ++i, ++m_index)
        {
            m_commands[m_index].CopyTo(&pUICommand[i]);
            ++fetched;
        }
        if (pceltFetched)
            *pceltFetched = fetched;
        return (fetched == celt) ? S_OK : S_FALSE;
    }

    IFACEMETHODIMP Skip(ULONG celt) override
    {
        m_index += celt;
        if (m_index > m_commands.size())
        {
            m_index = m_commands.size();
            return S_FALSE;
        }
        return S_OK;
    }

    IFACEMETHODIMP Reset() override
    {
        m_index = 0;
        return S_OK;
    }

    IFACEMETHODIMP Clone(IEnumExplorerCommand**) override
    {
        return E_NOTIMPL;
    }

private:
    std::vector<ComPtr<IExplorerCommand>> m_commands;
    size_t m_index = 0;
};

// ===========================================================================
// The root "Convert to" command. This is the CLSID registered in the manifest.
// ===========================================================================
class __declspec(uuid("EF3A7DDF-F221-42D9-9227-EB522E46F971")) ConvertRootCommand
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IExplorerCommand>
{
public:
    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* ppszName) override
    {
        return ::SHStrDupW(L"Convert to", ppszName);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* ppszIcon) override
    {
        // The icon must be resolved from THIS DLL's path, not the host
        // process path - the host is dllhost.exe.
        std::wstring resource = jalyro::ModulePath();
        if (resource.empty())
        {
            *ppszIcon = nullptr;
            return E_NOTIMPL;
        }
        resource += L",-101";
        return ::SHStrDupW(resource.c_str(), ppszIcon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* ppszInfotip) override
    {
        *ppszInfotip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* pguidCommandName) override
    {
        *pguidCommandName = GUID_NULL;
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray* psiItemArray,
                            BOOL /*fOkToBeSlow*/,
                            EXPCMDSTATE* pCmdState) override
    {
        // fOkToBeSlow is ignored on purpose. Even when Explorer says slow is
        // acceptable, we do the fast thing - opening files here is how shell
        // extensions earn a reputation for making Explorer hang.
        // EnumSubCommands is not given the selection, so the kind is captured
        // here and read there. Explorer calls GetState immediately before
        // EnumSubCommands on the same object, which is the same approach
        // PowerToys uses for its dynamic menus.
        m_kind = jalyro::KindOfSelection(psiItemArray);

        // Mixed selections are hidden too: images, audio and video share no
        // output format, so every entry we could offer would be wrong for part
        // of the selection.
        const bool supported = (m_kind != jalyro::MediaKind::Unsupported)
                            && (m_kind != jalyro::MediaKind::Mixed);

        *pCmdState = supported ? ECS_ENABLED : ECS_HIDDEN;

        // The single most useful diagnostic line in shell.log: if the menu is
        // not appearing, this tells you whether Explorer is even calling you.
        jalyro::Log(L"GetState -> %s", supported ? L"ECS_ENABLED" : L"ECS_HIDDEN");

        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* pFlags) override
    {
        *pFlags = ECF_HASSUBCOMMANDS;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** ppEnum) override
    {
        *ppEnum = nullptr;

        std::vector<ComPtr<IExplorerCommand>> commands;

        struct Entry { PCWSTR title; PCWSTR verb; EXPCMDFLAGS flags; };

        // One flyout level only - Explorer does not support nested
        // subcommands. Eight entries is the practical ceiling before the
        // flyout stops feeling native.
        static const Entry kImageEntries[] = {
            // Phase 0 finding: ECF_ISSEPARATOR renders as NOTHING inside an
            // IExplorerCommand flyout on Windows 11 - not a divider, not even
            // a blank line. The separator that used to sit between AVIF and
            // "Compress for email" has been removed because it was invisible.
            // Group by ordering instead; the 8-item ceiling makes that workable.
            { L"JPG",                L"jpg",      ECF_DEFAULT },
            { L"PNG",                L"png",      ECF_DEFAULT },
            { L"WEBP",               L"webp",     ECF_DEFAULT },
            { L"AVIF",               L"avif",     ECF_DEFAULT },
            { L"TIFF",               L"tiff",     ECF_DEFAULT },
            { L"Compress for email", L"email",    ECF_DEFAULT },
            { L"Settings\u2026",      L"settings", ECF_DEFAULT },
        };
        // Seven entries. Eight is the practical ceiling before a Win11 flyout
        // stops feeling native, and Explorer does not support a second level
        // of nesting - so there is no "More formats" escape hatch. One slot
        // left; spend it carefully.

        static const Entry kVideoEntries[] = {
            { L"MP4",                L"mp4",      ECF_DEFAULT },
            { L"WEBM",               L"webm",     ECF_DEFAULT },
            { L"Extract audio (MP3)", L"mp3",     ECF_DEFAULT },
            { L"Compress for email", L"compress", ECF_DEFAULT },
            { L"Discord-friendly",   L"discord",  ECF_DEFAULT },
            { L"Settings\u2026",      L"settings", ECF_DEFAULT },
        };

        static const Entry kAudioEntries[] = {
            { L"MP3",  L"mp3",  ECF_DEFAULT },
            { L"WAV",  L"wav",  ECF_DEFAULT },
            { L"FLAC", L"flac", ECF_DEFAULT },
            { L"M4A",  L"m4a",  ECF_DEFAULT },
            { L"OPUS", L"opus", ECF_DEFAULT },
            { L"Settings\u2026",      L"settings", ECF_DEFAULT },
        };

        // A mixed selection has NO valid common conversion. "Compress for
        // email" targets JPG, so a video in the selection would be reduced to a
        // single frame and an audio file would simply fail. Offering it was
        // worse than offering nothing.
        //
        // GetState hides the menu entirely for mixed selections, so this is
        // only reachable if the selection changed between calls.
        static const Entry kMixedEntries[] = {
            { L"Settings\u2026", L"settings", ECF_DEFAULT },
        };

        const Entry* entries = kImageEntries;
        size_t entryCount = ARRAYSIZE(kImageEntries);

        switch (m_kind)
        {
        case jalyro::MediaKind::Video:
            entries = kVideoEntries;  entryCount = ARRAYSIZE(kVideoEntries);  break;
        case jalyro::MediaKind::Audio:
            entries = kAudioEntries;  entryCount = ARRAYSIZE(kAudioEntries);  break;
        case jalyro::MediaKind::Mixed:
            entries = kMixedEntries;  entryCount = ARRAYSIZE(kMixedEntries);  break;
        default:
            break;   // images
        }

        for (size_t i = 0; i < entryCount; ++i)
        {
            const Entry& e = entries[i];

            ComPtr<SubCommand> cmd;
            HRESULT hr = MakeAndInitialize<SubCommand>(
                &cmd, e.title ? e.title : L"", e.verb, e.flags);
            if (FAILED(hr))
                return hr;

            ComPtr<IExplorerCommand> asCommand;
            hr = cmd.As(&asCommand);
            if (FAILED(hr))
                return hr;

            commands.push_back(std::move(asCommand));
        }

        // Capture the count BEFORE the move. v0.1.1 logged commands.size()
        // after std::move had emptied the vector, so it always said 0.
        const size_t count = commands.size();

        ComPtr<SubCommandEnum> enumerator;
        HRESULT hr = MakeAndInitialize<SubCommandEnum>(&enumerator, std::move(commands));
        if (FAILED(hr))
            return hr;

        jalyro::Log(L"EnumSubCommands -> %zu entries", count);
        return enumerator.CopyTo(ppEnum);
    }

    IFACEMETHODIMP Invoke(IShellItemArray*, IBindCtx*) override
    {
        // A command with subcommands is never itself invoked.
        return S_OK;
    }

private:
    // Set by GetState, read by EnumSubCommands. Defaults to Image so a menu
    // still renders if the call order is ever different from expected.
    jalyro::MediaKind m_kind = jalyro::MediaKind::Image;
};

// ===========================================================================
// Class factory.
//
// Written out by hand rather than using WRL's CoCreatableClass/Module macros.
// Those work, but they place the factory registration in a linker section,
// and when something goes wrong the failure looks identical to a bad manifest
// (CLSID not found). Since the whole point here is telling you WHICH layer
// is broken, an explicit factory with a log line is worth the 30 lines.
// ===========================================================================
class ClassFactory
    : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IClassFactory>
{
public:
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        *ppv = nullptr;
        if (outer)
            return CLASS_E_NOAGGREGATION;

        ComPtr<ConvertRootCommand> command = Make<ConvertRootCommand>();
        if (!command)
            return E_OUTOFMEMORY;

        return command.CopyTo(riid, ppv);
    }

    IFACEMETHODIMP LockServer(BOOL) override
    {
        return S_OK;
    }
};

} // namespace

// ===========================================================================
// COM entry points
// ===========================================================================

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, _Outptr_ void** ppv)
{
    *ppv = nullptr;

    if (!::IsEqualCLSID(rclsid, __uuidof(ConvertRootCommand)))
    {
        // Seeing this in the log means Windows found the DLL but the CLSID in
        // AppxManifest.xml does not match the one in this source file.
        jalyro::Log(L"DllGetClassObject: CLSID mismatch - manifest and source disagree");
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    ComPtr<ClassFactory> factory = Make<ClassFactory>();
    if (!factory)
        return E_OUTOFMEMORY;

    // First-call diagnostics, moved out of DllMain (loader lock). The "host="
    // field answers whether we are in dllhost.exe rather than explorer.exe.
    // std::call_once, not a plain bool: COM can call this concurrently, and an
    // unsynchronised read-modify-write on a function-local static is a data
    // race. Function-local static INITIALISATION is thread-safe; assignment
    // afterwards is not.
    static std::once_flag announceOnce;
    std::call_once(announceOnce, []
    {
        jalyro::Log(L"Loaded  module=%s", jalyro::ModulePath().c_str());
        jalyro::Log(L"  storage root = %s", jalyro::StorageRoot().c_str());
        jalyro::Log(L"  spool        = %s", jalyro::SpoolDirectory().c_str());
    });

    jalyro::Log(L"DllGetClassObject: handing out ConvertRootCommand factory");
    return factory.CopyTo(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    // Deliberately never unload. The surrogate host process is short-lived and
    // is torn down by the system anyway, so there is nothing to gain from
    // unloading - and plenty to lose, since unload races are a classic source
    // of shell extension crashes.
    return S_FALSE;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        // DllMain runs under the loader lock. Creating directories, allocating
        // and opening files here can deadlock or destabilise the host process -
        // and that host is a COM surrogate serving Explorer.
        //
        // Earlier versions logged from here. It worked, but that was luck: the
        // documented rule is to store the module handle and do nothing else.
        // The same diagnostics now happen on the first real call, in
        // DllGetClassObject, where no loader lock is held.
        jalyro::g_hInst = static_cast<HINSTANCE>(hModule);
        ::DisableThreadLibraryCalls(hModule);
        break;

    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}
