using Microsoft.JSInterop;

namespace BiLi_live_Tool.Services;

/// <summary>
/// Shared UI helpers for every page: toast notifications, a styled confirm dialog
/// and a busy/spinner state for buttons. The browser half lives in
/// wwwroot/blt-ui.js — a port of the Electron UI's toast()/confirm()/btnLoading()
/// helpers, which the rewritten Blazor UI was missing.
/// </summary>
public sealed class UiKit
{
    private readonly IJSRuntime _js;

    public UiKit(IJSRuntime js) => _js = js;

    /// <summary>Bottom-right toast; <paramref name="ok"/> false renders the red variant.</summary>
    public async Task ToastAsync(string msg, bool ok = true, int ms = 3200)
    {
        try { await _js.InvokeVoidAsync("bltToast", msg, ok, ms); } catch { }
    }

    /// <summary>Styled confirm dialog. Answers true when JS is unavailable (never block an action).</summary>
    public async Task<bool> ConfirmAsync(string msg, string okText = "确定", string cancelText = "取消")
    {
        try { return await _js.InvokeAsync<bool>("bltConfirm", msg, okText, cancelText); }
        catch { return true; }
    }

    /// <summary>Marks an element (by id) busy: spinner + pointer-events off, to stop double submits.</summary>
    public async Task BusyAsync(string elementId, bool busy)
    {
        try { await _js.InvokeVoidAsync("bltBusy", elementId, busy); } catch { }
    }
}
