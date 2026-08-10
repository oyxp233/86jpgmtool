#include "86JP.h"
#include "HookInterface.h"
#include "XLog.h"

#include <intrin.h>
#include <mutex>

#pragma comment(lib, "user32.lib")

static uintptr_t dnf_base = 0;
struct RaidGatePatchSite
{
    uintptr_t Offset;
    BYTE ExpectedDisplacement;
    DWORD OriginalProtect;
};

static bool ApplyRaidChannelDateGatePatch()
{
    RaidGatePatchSite sites[] =
    {
        { 0x00B1DB06, 0x17, 0 },
        { 0x00B1DB32, 0x17, 0 },
        { 0x00771F79, 0x07, 0 },
        { 0x007733C9, 0x09, 0 },
        { 0x01B25251, 0x25, 0 },
        { 0x01B2528B, 0x0C, 0 },
    };

    for (const auto& site : sites)
    {
        const auto address = reinterpret_cast<const BYTE*>(dnf_base + site.Offset);
        if (address[0] != 0x75 || address[1] != site.ExpectedDisplacement)
            return false;
    }

    size_t protectedCount = 0;
    for (auto& site : sites)
    {
        auto address = reinterpret_cast<BYTE*>(dnf_base + site.Offset);
        if (!VirtualProtect(address, 2, PAGE_EXECUTE_READWRITE, &site.OriginalProtect))
        {
            for (size_t index = 0; index < protectedCount; ++index)
            {
                DWORD ignored = 0;
                VirtualProtect(
                    reinterpret_cast<void*>(dnf_base + sites[index].Offset),
                    2,
                    sites[index].OriginalProtect,
                    &ignored);
            }
            return false;
        }
        ++protectedCount;
    }

    for (const auto& site : sites)
    {
        auto address = reinterpret_cast<BYTE*>(dnf_base + site.Offset);
        address[0] = 0xEB;
        FlushInstructionCache(GetCurrentProcess(), address, 2);
    }

    for (const auto& site : sites)
    {
        DWORD ignored = 0;
        VirtualProtect(
            reinterpret_cast<void*>(dnf_base + site.Offset),
            2,
            site.OriginalProtect,
            &ignored);
    }
    return true;
}

void __cdecl ProxyGameLog(int a1, wchar_t* source_path, wchar_t* function_name, int logType, wchar_t* Format, ...)
{
    wchar_t Buffer[512] = { 0 };
    wchar_t* dynamicBuffer = NULL;
    wchar_t* outputBuffer = Buffer;
    int bufferSize = _countof(Buffer);

    va_list ArgList;
    va_start(ArgList, Format);

    int result = _vswprintf_c_l(Buffer, bufferSize, Format, 0, ArgList);

    if (result < 0) {
        va_end(ArgList);
        va_start(ArgList, Format);

        int neededSize = _vscwprintf_l(Format, 0, ArgList) + 1;

        if (neededSize > 0) {
            dynamicBuffer = (wchar_t*)malloc(neededSize * sizeof(wchar_t));
            if (dynamicBuffer) {
                va_end(ArgList);
                va_start(ArgList, Format);
                _vswprintf_c_l(dynamicBuffer, neededSize, Format, 0, ArgList);
                outputBuffer = dynamicBuffer;
            }
        }
    }

    va_end(ArgList);

    if (outputBuffer) {
        AppendFileLogFormatLine(L"GameLog.log", L"[%s] [%d] [%s]", function_name, logType, outputBuffer);
    }

    if (dynamicBuffer) {
        free(dynamicBuffer);
    }
}

int __fastcall Proxy_CipherEncrypt(void* This, void* NotUsed, int packet_type, char* input, int in_size, char* out_put, int* out_size)
{
    *(int*)(input - 13 + 3) = in_size + 13;

    *out_size = in_size;
    memcpy(out_put, input, in_size);
    return 1;
}

static uintptr_t g_Ptr_SendMessageW = 0;
LRESULT WINAPI Proxy_SendMessageW(HWND hWnd, UINT Msg, WPARAM wParam, LPARAM lParam)
{
    if (Msg == 0x111 && wParam == 0x19F && lParam == 0)
        return 0;
    auto original = reinterpret_cast<decltype(&Proxy_SendMessageW)>(Hook_GetTrampoline(g_Ptr_SendMessageW));
    return original(hWnd, Msg, wParam, lParam);
}

unsigned int DelayHook(void*)
{
    do
    {
        Sleep(100);
    } while (nullptr == GetModuleHandleW(L"GameGaurd.dll"));

    Sleep(1000);
    Hook_Inline(reinterpret_cast<void*>(dnf_base + 0x01C11360), Proxy_CipherEncrypt);
    Hook_Inline(reinterpret_cast<void*>(dnf_base + 0x01CF9700), ProxyGameLog);

    return 0;
}

void PluginEntry()
{
    dnf_base = reinterpret_cast<uintptr_t>(GetModuleHandleW(L"DNF.exe"));

    ApplyRaidChannelDateGatePatch();

    DeleteFileW(L"GameLog.log");

    CreateThread(NULL, 0, (LPTHREAD_START_ROUTINE)DelayHook, NULL, 0, NULL);

    Hook_Inline(reinterpret_cast<void*>(dnf_base + 0x01CF9700), ProxyGameLog);
    Hook_Inline(reinterpret_cast<void*>(dnf_base + 0x01CF9800), ProxyGameLog);

    auto user32 = GetModuleHandleW(L"user32.dll");
    if (user32)
    {
        g_Ptr_SendMessageW = (uintptr_t)GetProcAddress(user32, "SendMessageW");
        Hook_Inline(reinterpret_cast<void*>(g_Ptr_SendMessageW), Proxy_SendMessageW);
    }
}

uintptr_t g_Ptr_GetStartupInfoW = 0;
VOID WINAPI Proxy_GetStartupInfoW(_Out_ LPSTARTUPINFOW lpStartupInfo)
{
    auto return_addr = (uintptr_t)_ReturnAddress();
    if (return_addr == dnf_base + 0x04AE71A5)
        PluginEntry();

    auto orifunc = reinterpret_cast<decltype(&Proxy_GetStartupInfoW)>(Hook_GetTrampoline(g_Ptr_GetStartupInfoW));
    orifunc(lpStartupInfo);
}

void JPEntry()
{
    dnf_base = reinterpret_cast<uintptr_t>(GetModuleHandleW(L"DNF.exe"));

    auto kernel32 = GetModuleHandleW(L"kernel32.dll");
    if (kernel32)
    {
        g_Ptr_GetStartupInfoW = (uintptr_t)GetProcAddress(kernel32, "GetStartupInfoW");
        Hook_Inline(reinterpret_cast<void*>(g_Ptr_GetStartupInfoW), Proxy_GetStartupInfoW);
    }
}
