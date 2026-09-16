using System;
using System.Threading.Tasks;
using DisCatSharp.CommandsNext;
using DisCatSharp.CommandsNext.Attributes;
using DisCatSharp.Entities;
using Serilog;

namespace WatcherBot.Utils;

[AttributeUsage(AttributeTargets.Method)]
public class RequireModeratorRoleInGuild : CheckBaseAttribute
{
    public override Task<bool> ExecuteCheckAsync(CommandContext ctx, bool help)
    {
        try
        {
            var botMain = (BotMain?)ctx.Services.GetService(typeof(BotMain));
            if (botMain is null || ctx.Member is not { } member)
            {
                Log.Logger.Error("Could not find output guild in moderator check");
                return Task.FromResult(false);
            }

            bool result = botMain.IsUserModerator(member) == IsModerator.Yes;
            Log.Logger.Debug("IsUserModerator: {Result}", result);
            return Task.FromResult(result);
        }
        catch (Exception exception)
        {
            return Task.FromException<bool>(exception);
        }
    }
}
