using System;
using System.Threading.Tasks;
using DisCatSharp.CommandsNext;
using DisCatSharp.CommandsNext.Attributes;
using DisCatSharp.Entities;
using DisCatSharp.Enums;

namespace WatcherBot.Utils;

[AttributeUsage(AttributeTargets.Method)]
public class RequirePermissionInGuild : CheckBaseAttribute
{
    private readonly Permissions permissions;

    public RequirePermissionInGuild(Permissions permissions)
    {
        this.permissions = permissions;
    }

    public override Task<bool> ExecuteCheckAsync(CommandContext ctx, bool help)
    {
        try
        {
            if (ctx.Member is not { } member)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(member?.Permissions.HasFlag(permissions) ?? false);
        }
        catch (Exception exception)
        {
            return Task.FromException<bool>(exception);
        }
    }
}
