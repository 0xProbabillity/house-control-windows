using System.Drawing;
using System.Text.Json.Nodes;
using HouseControl.Core;
using HouseControl.Core.Agent;
using HouseControl.Web;
using HouseControl.Windows;

namespace HouseControl;

/// <summary>AI Access: the private link an AI assistant uses to run the home.</summary>
internal sealed partial class App
{
    private AgentController? _agentController;
    private PageWindow? _agentWindow;

    private bool AgentLive => _agentController?.IsLive == true;

    private AgentController Agent
    {
        get
        {
            if (_agentController != null) return _agentController;
            _agentController = new AgentController(_plans,
                runInPanel: async request =>
                {
                    var answer = await _panel.Host.EvalAsync($"return await window.house.agent({Json.Serialize(request)});",
                        TimeSpan.FromSeconds(45));
                    return answer?.GetValue<string>() ?? "{}";
                },
                reloadAll: () =>
                {
                    ReloadAll();
                    _editor?.Host.Load();
                },
                copyText: text => Clipboard.SetText(text),
                openUrl: WebHost.OpenExternally,
                panelReady: () => _panel.Host.IsReady);
            _agentController.LiveChanged += live =>
            {
                _panel.Host.Call("agentLive", live);
                RefreshTray();
                RefreshSettings();
            };
            return _agentController;
        }
    }

    private void OpenAgent()
    {
        _panel.Hide();
        if (_agentWindow is null)
        {
            var host = Attach(new WebHost("agent", () =>
            {
                var boot = _plans.BootPayload();
                boot["agent"] = Agent.Status();
                return boot;
            }));
            _agentWindow = new PageWindow(host, "House Control — AI Access", new Size(460, 760), new Size(400, 560), _windows, "agent");
            Agent.AttachConsole((function, args) => host.Call(function, args));
            _agentWindow.FormClosed += (_, _) =>
            {
                _agentController?.DetachConsole();
                _agentWindow = null;
            };
            _ = Start(_agentWindow.InitializeAsync(), "agent");
        }
        _agentWindow.Present();
    }

    private void StartAgent() => Agent.Start();

    private void StopAgent() => _agentController?.Stop();

    /// <summary>The tunnel must not outlive the app.</summary>
    private void StopAgentForQuit() => _agentController?.Dispose();

    private bool HandleAgent(WebHost host, JsonObject message, string type)
    {
        if (_agentWindow is null || host != _agentWindow.Host) return false;
        if (type == "ready" || type.StartsWith("agent.", StringComparison.Ordinal))
        {
            Agent.HandleConsoleMessage(message);
            return true;
        }
        return false;
    }
}
