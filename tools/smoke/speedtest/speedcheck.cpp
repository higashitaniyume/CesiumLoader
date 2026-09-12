#include <windows.h>
#include <cstdio>
#include <cstdlib>

static void out(const char* s) { fprintf(stderr, "%s\n", s); fflush(stderr); }

int main()
{
    const char* dll = "C:\\src\\Study\\astralparty\\modding\\msvc\\src\\CesiumLoader\\bin\\Release\\version.dll";
    HMODULE h = LoadLibraryA(dll);
    if (!h) { char b[128]; sprintf(b, "LoadLibrary FAIL err=%lu", GetLastError()); out(b); return 1; }
    out("LoadLibrary OK");

    typedef BOOL(WINAPI* FnActive)();
    typedef double(WINAPI* FnGet)();
    typedef BOOL(WINAPI* FnSet)(double);
    auto active = (FnActive)GetProcAddress(h, "ap_speed_active");
    auto get = (FnGet)GetProcAddress(h, "ap_speed_get");
    auto set = (FnSet)GetProcAddress(h, "ap_speed_set");
    if (!active || !get || !set) { out("GetProcAddress FAIL"); return 1; }

    Sleep(2000); // 等 boot_thread: Sleep(1500) + speedhack_init

    char b[128];
    sprintf(b, "active = %d", active());
    out(b);
    sprintf(b, "speed  = %.1f", get());
    out(b);

    ULONGLONG t0 = GetTickCount64();
    Sleep(500);
    ULONGLONG v1 = GetTickCount64() - t0;
    sprintf(b, "1.0x virtual 500ms real -> %llu", v1);
    out(b);

    if (!set(2.0)) { out("set 2x FAIL"); return 1; }
    Sleep(50);
    t0 = GetTickCount64();
    Sleep(400);
    ULONGLONG v2 = GetTickCount64() - t0;
    sprintf(b, "2.0x virtual 400ms real -> %llu", v2);
    out(b);

    if (!set(0.5)) { out("set 0.5x FAIL"); return 1; }
    Sleep(50);
    t0 = GetTickCount64();
    Sleep(400);
    ULONGLONG v05 = GetTickCount64() - t0;
    sprintf(b, "0.5x virtual 400ms real -> %llu", v05);
    out(b);

    set(1.0);
    out("DONE");
    return 0;
}
