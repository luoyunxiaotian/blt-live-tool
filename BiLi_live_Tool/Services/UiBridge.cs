namespace BiLi_live_Tool.Services
{
    /// <summary>
    /// Lets non-UI services (the loopback debug bridge) run JS inside the hosted
    /// Blazor web view. The UI layer registers the handler on its first render;
    /// calls that arrive before that return a "not ready" note instead of
    /// throwing, so a headless probe never faults the request pipeline.
    /// </summary>
    public sealed class UiBridge
    {
        private Func<string, Task<string>>? _handler;

        /// <summary>Registered by MainLayout once the circuit has a JS runtime.</summary>
        public void Register(Func<string, Task<string>> handler) => _handler = handler;

        public void Unregister() => _handler = null;

        public Task<string> EvalAsync(string js)
            => _handler is { } h ? h(js) : Task.FromResult("ERR: UI 未就绪");
    }
}
