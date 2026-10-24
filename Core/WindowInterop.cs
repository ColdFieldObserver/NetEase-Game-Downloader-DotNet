namespace NeteaseGameDownloader.Core;

using System;
using System.Runtime.InteropServices;

/// <summary>
/// 窗口相关的原生 API（用于把已运行实例的窗口置前 / 还原）。
/// </summary>
internal static partial class WindowInterop
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    /// <summary>
    /// 把指定窗口还原（若最小化）并置前到用户面前。
    /// </summary>
    public static void BringToFront(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try
        {
            if (IsIconic(hWnd))
                ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
        }
        catch
        {
            /* 原生调用失败不应影响其它逻辑 */
        }
    }
}
