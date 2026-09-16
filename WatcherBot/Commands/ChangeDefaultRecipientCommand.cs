using System.Threading.Tasks;
using DisCatSharp.CommandsNext;
using DisCatSharp.CommandsNext.Attributes;
using DisCatSharp.Enums;
using Microsoft.Extensions.Options;
using WatcherBot.Config;
using WatcherBot.Utils;

namespace WatcherBot.Commands;

public class ChangeDefaultRecipientCommandModule : BaseCommandModule
{
    private readonly Config.Config config;

    public ChangeDefaultRecipientCommandModule(IOptions<Config.Config> cfg)
    {
        config = cfg.Value;
    }

    [Command("changedefaultrecipient")]
    [Description("Change the default recipient of appeals, i.e. the username that's provided when using !ban_anon")]
    [RequirePermissionInGuild(Permissions.BanMembers)]
    [RequireModeratorRoleInGuild]
    [RequireDmOrOutputGuild]
    public async Task ChangeDefaultRecipient(CommandContext context, [RemainingText] string? newRecipient = null)
    {
        if (!config.GuildSpecificConfigurations.TryGetValue(context.Guild.Id, out GuildConfig? guildConfig))
        {
            await context.Message.Channel.SendMessageAsync("Use this command within the server you want to change the default recipient of.");
            return;
        }

        if (string.IsNullOrWhiteSpace(newRecipient))
        {
            await context.Message.Channel.SendMessageAsync($"Current default appeal recipient is `{guildConfig.Templates.DefaultAppealRecipient}`.");
            return;
        }
        var prevRecipient = guildConfig.Templates.DefaultAppealRecipient;
        guildConfig.Templates.DefaultAppealRecipient = newRecipient;
        await context.Message.Channel.SendMessageAsync($"Default appeal recipient has been changed from `{prevRecipient}` to `{newRecipient}`.");
    }
}