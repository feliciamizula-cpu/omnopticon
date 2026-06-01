// Global app helpers used via Blazor JS interop (e.g. JS.InvokeVoidAsync("app.showAlert", ...)).
// Previously undefined, which threw "'app' was undefined" and tore down the Blazor circuit
// whenever any page tried to surface an alert.
window.app = window.app || {};

window.app.showAlert = function (message, type) {
    try {
        type = type || "info";
        const colors = {
            error: "#b00020",
            success: "#1b7f3b",
            warning: "#9a6700",
            info: "#1f4e8c"
        };

        let host = document.getElementById("app-toast-host");
        if (!host) {
            host = document.createElement("div");
            host.id = "app-toast-host";
            host.style.cssText =
                "position:fixed;top:16px;right:16px;z-index:99999;display:flex;flex-direction:column;gap:8px;max-width:360px;";
            document.body.appendChild(host);
        }

        const toast = document.createElement("div");
        toast.textContent = message;
        toast.style.cssText =
            "background:" + (colors[type] || colors.info) +
            ";color:#fff;padding:10px 14px;border-radius:6px;font:13px/1.4 system-ui,sans-serif;" +
            "box-shadow:0 4px 12px rgba(0,0,0,.25);opacity:0;transition:opacity .15s ease;";
        host.appendChild(toast);
        requestAnimationFrame(() => { toast.style.opacity = "1"; });

        setTimeout(() => {
            toast.style.opacity = "0";
            setTimeout(() => toast.remove(), 200);
        }, 4000);
    } catch (e) {
        // Never let a notification failure break the caller.
        console.warn("app.showAlert failed:", e, message);
    }
};
