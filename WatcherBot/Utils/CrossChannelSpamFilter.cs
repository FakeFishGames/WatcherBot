using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DisCatSharp;
using DisCatSharp.Entities;
using DisCatSharp.EventArgs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
// ReSharper disable InconsistentlySynchronizedField

namespace WatcherBot.Utils;

public class CrossChannelSpamFilter : LoopingTask
{
    private readonly record struct MessageInfo(DiscordMessage Message, DateTimeOffset Timestamp);
    private const int MaxDistinctChannels = 3;


    private static readonly TimeSpan DetectionWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MessageKeepDuration = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan TimedOutKeepDuration = TimeSpan.FromSeconds(30);

    private readonly object mutex = new();
    private readonly Dictionary<ulong, List<MessageInfo>> recentMessages = new();
    private readonly Dictionary<ulong, DateTimeOffset> timedOutUsers = new();

    public CrossChannelSpamFilter(BotMain botMain, IOptions<Config.Config> config) : base(botMain, config) { }

    protected override TimeSpan LoopFrequency
        => TimeSpan.FromSeconds(10);

    public override void Dispose()
    {
        base.Dispose();
        lock (mutex)
        {
            recentMessages.Clear();
            timedOutUsers.Clear();
        }

        GC.SuppressFinalize(this);
    }

    protected override Task LoopWork()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (mutex)
        {
            foreach ((ulong userId, DateTimeOffset timedOutAt) in timedOutUsers.ToArray())
            {
                if (now - timedOutAt >= TimedOutKeepDuration) { timedOutUsers.Remove(userId); }
            }

            foreach ((ulong userId, List<MessageInfo> messages) in recentMessages.ToArray())
            {
                if (messages.All(m => now - m.Timestamp >= MessageKeepDuration))
                {
                    recentMessages.Remove(userId);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task MessageCreated(DiscordClient sender, MessageCreateEventArgs args)
    {
        DiscordMessage message = args.Message;
        DiscordUser author = message.Author;

        if (author.IsBot || message.Channel is DiscordDmChannel) { return Task.CompletedTask; }

        if (!Config.GuildSpecificConfigurations.ContainsKey(message.Channel.GuildId ?? 0)) { return Task.CompletedTask; }

        //if (BotMain.IsUserModerator(author) == IsModerator.Yes) { return Task.CompletedTask; }

        if (BotMain.IsUserExemptFromSpamFilter(author) == IsExemptFromSpamFilter.Yes) { return Task.CompletedTask; }

        bool alreadyTimedOut;
        DiscordMessage[]? spamMessages = null;

        lock (mutex)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;

            alreadyTimedOut = timedOutUsers.TryGetValue(author.Id, out DateTimeOffset timedOutAt)
                              && now - timedOutAt < TimedOutKeepDuration;

            if (!alreadyTimedOut)
            {
                if (!recentMessages.TryGetValue(author.Id, out List<MessageInfo>? messages))
                {
                    messages = new List<MessageInfo>();
                    recentMessages.Add(author.Id, messages);
                }

                messages.RemoveAll(m => now - m.Timestamp >= MessageKeepDuration);
                messages.Add(new MessageInfo(message, now));

                int distinctChannels = messages.Where(m => now - m.Timestamp < DetectionWindow)
                                               .Select(static m => m.Message.ChannelId)
                                               .Distinct()
                                               .Count();

                if (distinctChannels >= MaxDistinctChannels)
                {
                    spamMessages = messages.Select(static m => m.Message).ToArray();
                    recentMessages.Remove(author.Id);
                    timedOutUsers[author.Id] = now;
                }
            }
        }

        if (alreadyTimedOut)
        {
            Task.Run(() => DeleteLateMessage(message));
        }
        else if (spamMessages is not null)
        {
            Task.Run(() => MuteSpammer(author, spamMessages));
        }

        return Task.CompletedTask;
    }

    private async Task MuteSpammer(DiscordUser user, DiscordMessage[] messages)
    {
        DiscordChannel[] channels = messages.Select(static m => m.Channel).DistinctBy(static c => c.Id).ToArray();

        Logger.LogInformation("Deleting messages sent by and muting {User} for reason {Reason} (sent {Count} messages in {ChannelCount} channels within {Window}s)",
                              user.UsernameWithDiscriminator,
                              MessageDeleters.MessageDeletionReason.PotentialSpam,
                              messages.Length,
                              channels.Length,
                              DetectionWindow.TotalSeconds);

        await BotMain.MuteUser(user, "Auto-detected spam messages");
        await Task.WhenAll(messages.Select(static m => m.DeleteAsync("Auto-detected spam message")));

        try
        {
            string reason =
                $"{messages.Length} messages sent in different channels in the last {DetectionWindow.TotalSeconds}s" +
                $" in {string.Join(", ", channels.Select(static c => c.Mention))}.";

            await BarotraumaToolBox.ReportSpam(BotMain, messages[0], reason, badWords: false);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Cross-channel spam filter failed to report {User}", user.UsernameWithDiscriminator);
        }
    }

    private async Task DeleteLateMessage(DiscordMessage message)
    {
        await message.DeleteAsync("Auto-detected spam message");

        if (BotMain.GetSpamReportChannel(message.Author) is { } reportChannel)
        {
            await reportChannel.SendMessageAsync($"New message from `{message.Author.Username}` found in {message.Channel.Mention} after being muted. Deleting...");
        }
    }
}