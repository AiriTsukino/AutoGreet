using AutoGreet.Models;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;

namespace AutoGreet.Services;

public sealed class TargetingService
{
    public string LastTargetStatus { get; private set; } = "No targeting attempted yet.";

    public async Task<bool> TargetAsync(VisitorKey target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return await DalamudServices.Framework.RunOnFrameworkThread(() => TargetOnFrameworkThread(target)).ConfigureAwait(false);
    }

    private bool TargetOnFrameworkThread(VisitorKey target)
    {
        var obj = FindPlayerObject(target);
        if (obj is null)
        {
            LastTargetStatus = $"Could not find visible player actor for {target.Display}.";
            DalamudServices.Log.Warning("AutoGreet could not target {Visitor}: no matching player object found.", target.Display);
            return false;
        }

        if (IsCurrentTarget(target))
        {
            LastTargetStatus = $"Already targeting {target.Display}.";
            return true;
        }

        DalamudServices.TargetManager.Target = obj;
        LastTargetStatus = $"Targeted {target.Display}.";
        return true;
    }

    private static bool IsCurrentTarget(VisitorKey target)
    {
        if (DalamudServices.TargetManager.Target is not IPlayerCharacter pc) return false;

        var name = pc.Name.ToString();
        if (!name.Equals(target.Name, StringComparison.OrdinalIgnoreCase)) return false;

        string world;
        try { world = pc.HomeWorld.Value.Name.ToString(); }
        catch { world = string.Empty; }

        return world.Equals(target.World, StringComparison.OrdinalIgnoreCase);
    }

    public static IGameObject? FindPlayerObject(VisitorKey target)
    {
        foreach (var obj in DalamudServices.ObjectTable.PlayerObjects)
        {
            if (obj is not IPlayerCharacter pc) continue;
            var name = pc.Name.ToString();
            if (!name.Equals(target.Name, StringComparison.OrdinalIgnoreCase)) continue;

            string world;
            try { world = pc.HomeWorld.Value.Name.ToString(); }
            catch { world = string.Empty; }

            if (!world.Equals(target.World, StringComparison.OrdinalIgnoreCase)) continue;
            return pc;
        }

        return null;
    }
}
