using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DisCatSharp;
using DisCatSharp.CommandsNext;
using DisCatSharp.Entities;
using DisCatSharp.Enums;
using DisCatSharp.EventArgs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;
using Serilog;
using WatcherBot.Config;
using WatcherBot.Utils;

namespace WatcherBot;

public class BotMain : IDisposable
{
    public readonly DiscordClient Client;

    private readonly Config.Config config;

    private readonly DuplicateMessageFilter duplicateMessageFilter;

    public readonly GitHubClient GitHubClient;

    private readonly ServiceProvider services;
    private readonly CancellationTokenSource shutdownRequest;
    private readonly ThreadKeepAlive threadKeepAlive;

    public BotMain()
    {
        IConfigurationRoot configurationRoot = new ConfigurationBuilder()
                                               .AddJsonFile("appsettings.json", false, false)
                                               .Build();
        services = new ServiceCollection()
                   .AddSingleton(this)
                   .AddSingleton(configurationRoot)
                   .AddOptions()
                   .Configure<Config.Config>(configurationRoot.GetSection(Config.Config.ConfigSection),
                                             binder => binder.BindNonPublicProperties = true)
                   .BuildServiceProvider();

        var configOptions = services.GetRequiredService<IOptions<Config.Config>>();
        config     = configOptions.Value;
        Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(configurationRoot).CreateLogger();

        Log.Logger.Information("Starting bot");
        shutdownRequest = new CancellationTokenSource();

        //GitHub API
        GitHubClient = new GitHubClient(new ProductHeaderValue("WatcherBot"));
        var gitHubCredentials = new Credentials(config.GitHubToken);
        GitHubClient.Credentials = gitHubCredentials;
        GitHubClient.SetRequestTimeout(TimeSpan.FromSeconds(5));

        //Discord API
        var discordConfiguration = new DiscordConfiguration
        {
            Token         = config.DiscordApiToken,
            TokenType     = TokenType.Bot,
            Intents       = DiscordIntents.AllUnprivileged | DiscordIntents.MessageContent,
            AutoReconnect = true,
            LoggerFactory = new LoggerFactory().AddSerilog(Log.Logger),
        };
        Client = new DiscordClient(discordConfiguration);

        Client.MessageCreated += HandleCommand;
        MessageDeleters deleters = new(this, configOptions);
        Client.MessageCreated += deleters.ContainsDisallowedInvite;
        Client.MessageCreated += deleters.DeleteCringeMessages;
        Client.MessageCreated += deleters.MessageWithinAttachmentLimits;
        Client.MessageCreated += deleters.ProhibitFormattingFromUsers;
        Client.MessageCreated += deleters.DeleteBadWords;
        Client.MessageCreated += deleters.DeletePotentialSpam;
        Client.MessageCreated += deleters.ReplyInNoConversationChannel;

        duplicateMessageFilter =  new DuplicateMessageFilter(this, configOptions);
        Client.MessageCreated  += duplicateMessageFilter.MessageCreated;
        duplicateMessageFilter.Start();

        threadKeepAlive = new ThreadKeepAlive(this, configOptions);
        threadKeepAlive.Start();

        CommandsNextConfiguration commandsConfig = new()
        {
            DmHelp                   = true,
            EnableMentionPrefix      = true,
            ServiceProvider          = services,
            StringPrefixes           = new List<string> { "!" },
            UseDefaultCommandHandler = false,
        };
        CommandsNextExtension commands = Client.UseCommandsNext(commandsConfig);
        commands.CommandExecuted += Logging.Logging.CommandExecuted;
        commands.CommandErrored  += Logging.Logging.CommandErrored;
        commands.RegisterConverter(new DateTimeConverter());
        commands.RegisterCommands(Assembly.GetAssembly(typeof(BotMain))!);

        commandHandler = typeof(CommandsNextExtension).GetMethod("HandleCommandsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.CreateDelegate<CommandHandler>(commands)
                ?? throw new Exception("Failed to create delegate for CommandsNextExtension.HandleCommandsAsync!");
    }

    public DiscordGuild? GetOutputGuild(DiscordUser user)
    {
        if (!GetMemberFromUser(user, out DiscordMember? member))
        {
            Log.Logger.Warning("{GetOutputGuildName}: Unable to get output guild from {DiscordUser}", nameof(GetOutputGuild), user);
            return null;
        }

        return member.Guild;
    }

    public DiscordRole? GetMutedRole(DiscordUser user)
    {
        if (!GetMemberFromUser(user, out DiscordMember? member) ||
            !config.GuildSpecificConfigurations.TryGetValue(member.Guild.Id, out GuildConfig? c))
        {
            Log.Logger.Warning("{GetMutedRole}: Unable to get output guild from {DiscordUser}", nameof(GetMutedRole), user);
            return null;
        }

        return member.Guild.Roles.GetValueOrDefault(c.MutedRole);
    }

    public DiscordChannel? GetSpamReportChannel(DiscordUser user)
    {
        if (!GetMemberFromUser(user, out DiscordMember? member) ||
            !config.GuildSpecificConfigurations.TryGetValue(member.Guild.Id, out GuildConfig? c))
        {
            Log.Logger.Warning("{GetSpamReportChannel}: Unable to get output guild from {DiscordUser}", nameof(GetSpamReportChannel), user);
            return null;
        }

        return member.Guild.Channels.GetValueOrDefault(c.SpamReportChannel);
    }

    public void Dispose()
    {
        Client.Dispose();
        duplicateMessageFilter.Cancel();
        duplicateMessageFilter.Dispose();
        threadKeepAlive.Cancel();
        threadKeepAlive.Dispose();
        services.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Kill() => shutdownRequest.Cancel();

    public IsModerator IsUserModerator(DiscordUser user)
    {
        if (!GetMemberFromUser(user, out DiscordMember? member) ||
            !config.GuildSpecificConfigurations.TryGetValue(member.Guild.Id, out var c)) { return IsModerator.No; }

        return c.ModeratorRoleIds.Overlaps(member.Roles.Select(static r => r.Id)) ? IsModerator.Yes : IsModerator.No;
    }


    public bool GetMemberFromUser(DiscordUser user, [NotNullWhen(true)] out DiscordMember? member)
    {
        member = user as DiscordMember;
        return member is not null;
    }

    public IsExemptFromSpamFilter IsUserExemptFromSpamFilter(DiscordUser user)
    {
        if (!GetMemberFromUser(user, out DiscordMember? member) ||
            !config.GuildSpecificConfigurations.TryGetValue(member.Guild.Id, out var c)) { return IsExemptFromSpamFilter.No; }

        return member.Roles.Any(r => r.Id == c.SpamFilterExemptionRole)
                   ? IsExemptFromSpamFilter.Yes
                   : IsExemptFromSpamFilter.No;
    }

    public async Task MuteUser(DiscordUser user, string reason)
    {
        if (!GetMemberFromUser(user, out DiscordMember? member) ||
            !config.GuildSpecificConfigurations.TryGetValue(member.Guild.Id, out GuildConfig? c)) { return; }

        if (!member.Guild.Roles.TryGetValue(c.MutedRole, out DiscordRole? role)) { return; }
        await member.GrantRoleAsync(role, reason);
    }

    private delegate Task CommandHandler(DiscordClient sender, MessageCreateEventArgs args);
    private readonly CommandHandler commandHandler;
    private Task HandleCommand(DiscordClient sender, MessageCreateEventArgs args)
    {
        if (!config.ProhibitCommandsFromUsers.Contains(args.Author.Id))
        {
            return commandHandler(sender, args);
        }

        return Task.CompletedTask;
    }

    public async Task MainAsync()
    {
        await Client.ConnectAsync();
        while (!shutdownRequest.IsCancellationRequested)
        {
            await Task.Delay(1000);
        }

        await Client.UpdateStatusAsync(null, UserStatus.Offline);
        await Client.DisconnectAsync();
        await Task.Delay(2500);
        Dispose();
    }
}
