using System;
using System.Threading.Tasks;
using DisCatSharp.CommandsNext;
using DisCatSharp.CommandsNext.Attributes;
using DisCatSharp.Entities;
using DisCatSharp.Enums;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using WatcherBot.Config;
using WatcherBot.Utils;

namespace WatcherBot.Commands;

public class TimeoutCommandModule : BaseCommandModule
{
    private readonly Config.Config config;

    public TimeoutCommandModule(IOptions<Config.Config> cfg)
    {
        config = cfg.Value;
    }

    private async Task Timeout(CommandContext          context, DiscordMember member, DateTime time,
                               string? reason,  Anonymous     anon)
    {
        if (!config.GuildSpecificConfigurations.TryGetValue(member.Guild.Id, out GuildConfig? guildConfig)) { return; }

        if (config.TestMode)
        {
            Log.Logger.Error("Attempted to timeout but test mode is enabled.");
            return;
        }

        try
        {
            await member.TimeoutAsync(time, reason);
        }
        catch (Exception e)
        {
            await context.Message.Channel
                         .SendMessageAsync($"Error timing out {member.Mention}: {(e.InnerException ?? e).Message}");
            return;
        }

        var unixStr = new DateTimeOffset(time).ToUnixTimeSeconds().ToString();

        string msg = guildConfig.Templates.Timeout.Replace("[time]", unixStr)
                                .Replace("[timeouter]", guildConfig.Templates.GetAppealRecipients(context.User.QueryableName(), anon))
                                .Replace("[reason]", reason ?? "No reason provided");

        var appeal = $"The appeal message sent was:\n{msg}";
        try
        {
            await member.SendMessageAsync(msg);
        }
        catch
        {
            appeal = "The appeal message could not be sent.";
        }

        await context.Message.Channel.SendMessageAsync($"Timed out {member.Mention} until <t:{unixStr}>. {appeal}");
    }

    [Command("timeout")]
    [Description("Timeout a member and send them an appeal message via DMs, including your username for contact.")]
    [RequirePermissionInGuild(Permissions.ModerateMembers)]
    [RequireModeratorRoleInGuild]
    [RequireDmOrOutputGuild]
    public async Task Timeout(CommandContext          context, DiscordMember member, DateTime time,
                              [RemainingText] string? reason = null) =>
        await Timeout(context, member, time, reason, Anonymous.No);

    [Command("timeout_anon")]
    [Description("Timeout a member and send them an appeal message via DMs with a default username for contact.")]
    [RequirePermissionInGuild(Permissions.ModerateMembers)]
    [RequireModeratorRoleInGuild]
    [RequireDmOrOutputGuild]
    public async Task TimeoutAnon(CommandContext          context, DiscordMember member, DateTime time,
                                  [RemainingText] string? reason = null) =>
        await Timeout(context, member, time, reason, Anonymous.Yes);
}
