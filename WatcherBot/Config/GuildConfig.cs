using System.Collections.Generic;
using WatcherBot.Utils;

namespace WatcherBot.Config;

public class GuildConfig
{
    private HashSet<ulong> moderatorRoleIds { get; } = new();

    public IReadOnlySet<ulong> ModeratorRoleIds
        => moderatorRoleIds;

    public ulong MutedRole { get; init; }

    public ulong SpamFilterExemptionRole { get; init; }
    public ulong SpamReportChannel { get; init; }

    public Templates Templates { get; init; } = new();
}