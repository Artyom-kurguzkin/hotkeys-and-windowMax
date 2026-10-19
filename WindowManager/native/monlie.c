// monlie.dll - the "monitor lie" that makes a managed app believe its monitor is the
// slot the window manager assigned it. Injected into every GUI process via a
// system-wide WH_GETMESSAGE hook installed by WindowManager.exe.
//
// Mechanism (the one that proved reliable in the spike): the manager creates a
// per-process file mapping "Local\monlie_<pid>" holding {enabled, slot}. When enabled,
// this DLL overwrites every GetMonitorInfo result in that process to the slot. The app
// then enters its own fullscreen (F11, etc.) and sizes itself to the slot, hiding its
// own chrome - no frame stripping, no per-app crop heights.
//
// Why per-PROCESS and not per-window: an app's fullscreen sizing reads the monitor
// bounds through the real HMONITOR (verified: a synthetic per-window handle is ignored
// for sizing). So the lie must come back for the real handle, which is process-wide.
// Common case (one managed window per process) is exact; the ceiling is several windows
// of ONE app in DIFFERENT slots at once - they share the process slot. The manager sets
// the slot to the window it is fullscreening.
//
// Build: see build-native.ps1 (mingw-w64 gcc + vendored MinHook).
#include <windows.h>
#include <stdio.h>
#include "MinHook.h"

typedef struct { volatile LONG enabled; RECT slot; } MonLieShared;
static MonLieShared *g_shm;
static DWORD g_lastTry;
static BOOL g_ready;

typedef BOOL (WINAPI *GMI)(HMONITOR, LPMONITORINFO);
static GMI real_GMIW, real_GMIA;

// Open the per-process mapping lazily: the manager may tag this process AFTER the hook
// injected us. Retry at most ~every 2s so unmanaged processes don't spin on OpenFileMapping.
static void ensure_shm(void) {
    if (g_shm) return;
    DWORD now = GetTickCount();
    if (g_lastTry && now - g_lastTry < 2000) return;
    g_lastTry = now;
    char name[64]; sprintf(name, "Local\\monlie_%lu", GetCurrentProcessId());
    HANDLE h = OpenFileMappingA(FILE_MAP_READ, FALSE, name);
    if (h) { g_shm = (MonLieShared*)MapViewOfFile(h, FILE_MAP_READ, 0, 0, sizeof(MonLieShared)); CloseHandle(h); }
}

static void lie(LPMONITORINFO mi) {
    ensure_shm();
    if (g_shm && g_shm->enabled && mi && mi->cbSize >= sizeof(MONITORINFO)) {
        mi->rcMonitor = g_shm->slot; mi->rcWork = g_shm->slot; mi->dwFlags = MONITORINFOF_PRIMARY;
    }
}
static BOOL WINAPI my_GMIW(HMONITOR m, LPMONITORINFO mi) { BOOL r = real_GMIW(m, mi); if (r) lie(mi); return r; }
static BOOL WINAPI my_GMIA(HMONITOR m, LPMONITORINFO mi) { BOOL r = real_GMIA(m, mi); if (r) lie(mi); return r; }

// Processes we never touch: the shell and the manager itself.
static BOOL excluded_host(void) {
    char path[MAX_PATH] = ""; GetModuleFileNameA(NULL, path, MAX_PATH);
    const char *base = path; for (const char *p = path; *p; p++) if (*p == '\\' || *p == '/') base = p + 1;
    return _stricmp(base, "explorer.exe") == 0 || _stricmp(base, "WindowManager.exe") == 0;
}

static void setup(void) {
    if (excluded_host()) return;
    if (MH_Initialize() != MH_OK) return;
    HMODULE u = GetModuleHandleA("user32.dll");
    MH_CreateHook(GetProcAddress(u, "GetMonitorInfoW"), (void*)my_GMIW, (void**)&real_GMIW);
    MH_CreateHook(GetProcAddress(u, "GetMonitorInfoA"), (void*)my_GMIA, (void**)&real_GMIA);
    if (MH_EnableHook(MH_ALL_HOOKS) == MH_OK) g_ready = TRUE;
}

// The hook proc only needs to exist so the DLL maps into target processes.
__declspec(dllexport) LRESULT CALLBACK GetMsgProc(int code, WPARAM w, LPARAM l) { return CallNextHookEx(NULL, code, w, l); }

BOOL APIENTRY DllMain(HMODULE h, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) { DisableThreadLibraryCalls(h); setup(); }
    else if (reason == DLL_PROCESS_DETACH) {
        // Restore user32's bytes BEFORE this DLL unmaps, or the inline JMP left in the
        // hooked functions points into freed memory and the host crashes on its next
        // monitor query. (Learned the hard way: an uncleaned spike closed Vivaldi.)
        if (g_ready) { MH_DisableHook(MH_ALL_HOOKS); MH_Uninitialize(); }
    }
    return TRUE;
}
