using Clircs.Identity;
using Jint;
using Jint.Native;
using System.Text.Json;

namespace Clircs.Scripting;

internal sealed partial class ScriptInstance
{
    private string? WindowActive()
    {
        Demand(ScriptPermission.Output);
        return _services.Windows.Active()?.Value.ToString();
    }

    private string? WindowStatus(JsValue networkId)
    {
        Demand(ScriptPermission.Output);
        return _services.Windows.Status(ReadWindowNetworkId(networkId))?.Value.ToString();
    }

    private string? WindowFind(JsValue name, JsValue networkId)
    {
        Demand(ScriptPermission.Output);
        return _services.Windows.Find(
            ReadWindowName(name),
            ReadWindowNetworkId(networkId))?.Value.ToString();
    }

    private string WindowList()
    {
        Demand(ScriptPermission.Output);
        var windows = _services.Windows.List(_script.Manifest.Id);
        return JsonSerializer.Serialize(windows.Select(window => new
        {
            bufferId = window.BufferId.Value.ToString(),
            name = window.Name,
            networkId = window.NetworkId?.Value.ToString()
        }));
    }

    private string WindowOpen(JsValue name, JsValue networkId)
    {
        Demand(ScriptPermission.Output);
        return _services.Windows.Open(
            _script.Manifest.Id,
            ReadWindowName(name),
            ReadWindowNetworkId(networkId)).Value.ToString();
    }

    private static string ReadWindowName(JsValue value)
    {
        if (!value.IsString())
        {
            throw new ArgumentException("Window names must be nonempty text.");
        }

        var name = value.AsString();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }

    private static NetworkSessionId? ReadWindowNetworkId(JsValue value)
    {
        if (value.IsNull() || value.IsUndefined()) return null;

        if (!value.IsString() ||
            !Guid.TryParse(value.AsString(), out var id) ||
            id == Guid.Empty)
        {
            throw new ArgumentException(
                "networkId must be a valid network ID or null.");
        }

        return new NetworkSessionId(id);
    }
}
