// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Runtime.InteropServices;

namespace Grog.App.Services;

/// <summary>The minimum Objective-C interop Grog needs on macOS: reading and writing an NSWindow's
/// styleMask (to clear NSWindowStyleMaskMiniaturizable - native minimize is broken in the Avalonia mac
/// backend, see MainWindow's mac block). DllImports only ever execute behind OperatingSystem.IsMacOS()
/// guards, so this file is safely cross-platform.</summary>
internal static class MacInterop
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(LibObjC)]
    private static extern IntPtr sel_registerName(string name);

    // objc_msgSend has no fixed signature; each call shape needs its own P/Invoke. These two cover
    // `-(NSUInteger)styleMask` and `-(void)setStyleMask:(NSUInteger)`.
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern nuint objc_msgSend_ret_nuint(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_nuint(IntPtr receiver, IntPtr selector, nuint arg);

    public static nuint GetStyleMask(IntPtr nsWindow)
        => objc_msgSend_ret_nuint(nsWindow, sel_registerName("styleMask"));

    public static void SetStyleMask(IntPtr nsWindow, nuint mask)
        => objc_msgSend_void_nuint(nsWindow, sel_registerName("setStyleMask:"), mask);
}
