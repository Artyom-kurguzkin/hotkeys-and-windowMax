// monlie.dll - the "monitor lie" that makes a managed app believe its monitor is the
// slot the window manager assigned it. Injected into every GUI process via a
// system-wide WH_GETMESSAGE hook installed by WindowManager.exe.
//
// Mechanism (the one that proved reliable in the spike): the manager creates a
// per-process file mapping "Local\monlie_<pid>" holding a small table of
// {monitor handle, enabled, slot}. GetMonitorInfo for a monitor that has an enabled
// entry returns that entry's slot as the monitor's bounds; every other monitor is
// reported truthfully. The app then enters its own fullscreen (F11, etc.) and sizes
// itself to the slot, hiding its own chrome - no frame stripping, no per-app crop heights.
//
// Why keyed by the REAL monitor handle: an app's fullscreen sizing reads the monitor
// bounds through the real HMONITOR of the monitor the window is on (verified: a synthetic
// per-window handle is ignored for sizing; Chromium keeps a display list from
// EnumDisplayMonitors + GetMonitorInfo and maps a window to it via MonitorFromWindow).
// So the lie must come back for the real handle. One slot PER MONITOR per process lets
// several windows of one app live on different monitors in different slots. The first
// version lied the same slot for every monitor of the process: with VS Code windows on
// three monitors, Alt+Tab onto one re-slotted them all and they jumped between displays
// (measured in the real log, 2026-10-31). The remaining ceiling: two windows of ONE app
// on the SAME monitor in different slots share that monitor's entry - the last one set wins.
//
// Build: see build-native.ps1 (mingw-w64 gcc + vendored MinHook).
#include <windows.h>
#include <stdio.h>
#include "MinHook.h"

// Layout shared with MonitorLie.cs (MonitorLie.Slots / EntrySize): keep both in step.
#define MONLIE_SLOTS 8
typedef struct { LONGLONG monitor; volatile LONG enabled; LONG reserved; RECT slot; } MonLieEntry;
typedef struct { MonLieEntry e[MONLIE_SLOTS]; } MonLieShared;
_Static_assert(sizeof(MonLieEntry) == 32, "MonLieEntry must be 32 bytes (MonitorLie.cs assumes it)");
_Static_assert(sizeof(MonLieShared) == 256, "MonLieShared must be 256 bytes (MonitorLie.cs assumes it)");

static MonLieShared *g_shm;
static DWORD g_lastTry;
static BOOL g_ready;

typedef BOOL (WINAPI *GMI)(HMONITOR, LPMONITORINFO);
static GMI real_GMIW, real_GMIA;

// Open the per-process mapping lazily: the manager may tag this process AFTER the hook
// injected us. Retry every ~300ms (OpenFileMapping on a missing name is cheap) - a 2s
// throttle lost a race with the manager's probe, which sends F11 ~400ms after SetSlot, so
// the app fullscreened to the REAL monitor and was wrongly marked not-capable.
static void ensure_shm(void) {
    if (g_shm) return;
    DWORD now = GetTickCount();
    if (g_lastTry && now - g_lastTry < 300) return;
    g_lastTry = now;
    char name[64]; sprintf(name, "Local\\monlie_%lu", GetCurrentProcessId());
    HANDLE h = OpenFileMappingA(FILE_MAP_READ, FALSE, name);
    if (h) { g_shm = (MonLieShared*)MapViewOfFile(h, FILE_MAP_READ, 0, 0, sizeof(MonLieShared)); CloseHandle(h); }
}

// Only the monitor with an enabled entry is lied about; its flags (primary or not) stay real.
static void lie(HMONITOR m, LPMONITORINFO mi) {
    ensure_shm();
    if (!g_shm || !mi || mi->cbSize < sizeof(MONITORINFO)) return;
    for (int i = 0; i < MONLIE_SLOTS; i++) {
        const MonLieEntry *e = &g_shm->e[i];
        if (e->enabled && (HMONITOR)(ULONG_PTR)e->monitor == m) { mi->rcMonitor = e->slot; mi->rcWork = e->slot; return; }
    }
}
static BOOL WINAPI my_GMIW(HMONITOR m, LPMONITORINFO mi) { BOOL r = real_GMIW(m, mi); if (r) lie(m, mi); return r; }
static BOOL WINAPI my_GMIA(HMONITOR m, LPMONITORINFO mi) { BOOL r = real_GMIA(m, mi); if (r) lie(m, mi); return r; }

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
