// Shaderless (Linux/Proton): dxgi.dll companion proxy for NVIDIA Streamline (sl.interposer.dll) games.
// Streamline games do not statically import d3d12.dll (they load sl.interposer.dll / sl.common.dll / _nvngx.dll,
// which load C:\Windows\System32\d3d12.dll first and bypass <GameDir>\d3d12.dll). They do statically import dxgi.dll,
// and Proton sets WINEDLLOVERRIDES="dxgi=n" by default. Loading <GameDir>\dxgi.dll at process startup preloads our
// sibling <GameDir>\d3d12.dll recorder before slInit runs, while forwarding all DXGI exports to system32\dxgi.dll.
#define NOMINMAX
#include <windows.h>
#include <unknwn.h>
#include <mutex>
#include <string>

static HMODULE g_real;
static std::wstring g_dir;

using PFN_CreateDXGIFactory = HRESULT(WINAPI*)(REFIID, void**);
using PFN_CreateDXGIFactory1 = HRESULT(WINAPI*)(REFIID, void**);
using PFN_CreateDXGIFactory2 = HRESULT(WINAPI*)(UINT, REFIID, void**);
using PFN_DXGIGetDebugInterface1 = HRESULT(WINAPI*)(UINT, REFIID, void**);
using PFN_DXGIDeclareAdapterRemovalSupport = HRESULT(WINAPI*)();
using PFN_DXGID3D10CreateDevice = HRESULT(WINAPI*)(HMODULE, void*, void*, UINT, const void*, UINT, void**);
using PFN_DXGID3D10RegisterLayers = HRESULT(WINAPI*)(const void*, UINT);

static PFN_CreateDXGIFactory real_CreateDXGIFactory;
static PFN_CreateDXGIFactory1 real_CreateDXGIFactory1;
static PFN_CreateDXGIFactory2 real_CreateDXGIFactory2;
static PFN_DXGIGetDebugInterface1 real_DXGIGetDebugInterface1;
static PFN_DXGIDeclareAdapterRemovalSupport real_DXGIDeclareAdapterRemovalSupport;
static PFN_DXGID3D10CreateDevice real_DXGID3D10CreateDevice;
static PFN_DXGID3D10RegisterLayers real_DXGID3D10RegisterLayers;

static void ensure_recorder() {
    static std::once_flag once;
    std::call_once(once, [] {
        if (!g_dir.empty()) {
            LoadLibraryW((g_dir + L"d3d12.dll").c_str());
        }
    });
}

extern "C" HRESULT WINAPI Proxy_CreateDXGIFactory(REFIID riid, void** ppFactory) {
    ensure_recorder();
    return real_CreateDXGIFactory ? real_CreateDXGIFactory(riid, ppFactory) : E_NOINTERFACE;
}

extern "C" HRESULT WINAPI Proxy_CreateDXGIFactory1(REFIID riid, void** ppFactory) {
    ensure_recorder();
    return real_CreateDXGIFactory1 ? real_CreateDXGIFactory1(riid, ppFactory) : E_NOINTERFACE;
}

extern "C" HRESULT WINAPI Proxy_CreateDXGIFactory2(UINT flags, REFIID riid, void** ppFactory) {
    ensure_recorder();
    return real_CreateDXGIFactory2 ? real_CreateDXGIFactory2(flags, riid, ppFactory) : E_NOINTERFACE;
}

extern "C" HRESULT WINAPI Proxy_DXGIGetDebugInterface1(UINT flags, REFIID riid, void** pDebug) {
    ensure_recorder();
    return real_DXGIGetDebugInterface1 ? real_DXGIGetDebugInterface1(flags, riid, pDebug) : E_NOINTERFACE;
}

extern "C" HRESULT WINAPI Proxy_DXGIDeclareAdapterRemovalSupport() {
    ensure_recorder();
    return real_DXGIDeclareAdapterRemovalSupport ? real_DXGIDeclareAdapterRemovalSupport() : S_OK;
}

extern "C" HRESULT WINAPI Proxy_DXGID3D10CreateDevice(HMODULE d3d10core, void* factory, void* adapter, UINT flags, const void* fl, UINT fl_count, void** ppDevice) {
    ensure_recorder();
    return real_DXGID3D10CreateDevice ? real_DXGID3D10CreateDevice(d3d10core, factory, adapter, flags, fl, fl_count, ppDevice) : E_NOINTERFACE;
}

extern "C" HRESULT WINAPI Proxy_DXGID3D10RegisterLayers(const void* layers, UINT num_layers) {
    ensure_recorder();
    return real_DXGID3D10RegisterLayers ? real_DXGID3D10RegisterLayers(layers, num_layers) : E_NOINTERFACE;
}

// Marker export recognized by ShaderEngine.IsOurProxy.
extern "C" void WINAPI Shaderless_StartWarm(IUnknown*) {}

BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID) {
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(self);
    wchar_t p[MAX_PATH];
    std::wstring dir(p, GetModuleFileNameW(self, p, MAX_PATH));
    g_dir = dir.substr(0, dir.find_last_of(L"\\/") + 1);
    if (GetFileAttributesW((g_dir + L"shaderless.ini").c_str()) == INVALID_FILE_ATTRIBUTES &&
        GetFileAttributesW((g_dir + L"scskiller.ini").c_str()) == INVALID_FILE_ATTRIBUTES) {
        wchar_t exe_p[MAX_PATH];
        std::wstring exe_dir(exe_p, GetModuleFileNameW(nullptr, exe_p, MAX_PATH));
        exe_dir = exe_dir.substr(0, exe_dir.find_last_of(L"\\/") + 1);
        if (GetFileAttributesW((exe_dir + L"shaderless.ini").c_str()) != INVALID_FILE_ATTRIBUTES ||
            GetFileAttributesW((exe_dir + L"scskiller.ini").c_str()) != INVALID_FILE_ATTRIBUTES ||
            GetFileAttributesW((exe_dir + L"d3d12.dll").c_str()) != INVALID_FILE_ATTRIBUTES) {
            g_dir = exe_dir;
        }
    }
    GetSystemDirectoryW(p, MAX_PATH);
    g_real = LoadLibraryW((std::wstring(p) + L"\\dxgi.dll").c_str());
    if (!g_real) return FALSE;
    real_CreateDXGIFactory = (PFN_CreateDXGIFactory)GetProcAddress(g_real, "CreateDXGIFactory");
    real_CreateDXGIFactory1 = (PFN_CreateDXGIFactory1)GetProcAddress(g_real, "CreateDXGIFactory1");
    real_CreateDXGIFactory2 = (PFN_CreateDXGIFactory2)GetProcAddress(g_real, "CreateDXGIFactory2");
    real_DXGIGetDebugInterface1 = (PFN_DXGIGetDebugInterface1)GetProcAddress(g_real, "DXGIGetDebugInterface1");
    real_DXGIDeclareAdapterRemovalSupport = (PFN_DXGIDeclareAdapterRemovalSupport)GetProcAddress(g_real, "DXGIDeclareAdapterRemovalSupport");
    real_DXGID3D10CreateDevice = (PFN_DXGID3D10CreateDevice)GetProcAddress(g_real, "DXGID3D10CreateDevice");
    real_DXGID3D10RegisterLayers = (PFN_DXGID3D10RegisterLayers)GetProcAddress(g_real, "DXGID3D10RegisterLayers");
    // Preload our sibling d3d12.dll recorder at DLL_PROCESS_ATTACH before WinMain / slInit runs
    ensure_recorder();
    return TRUE;
}
