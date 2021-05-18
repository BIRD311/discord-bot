﻿using System;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using MomentumDiscordBot.Constants;
using MomentumDiscordBot.Services;

namespace MomentumDiscordBot.Commands.Admin
{
    public class AdminModule : AdminModuleBase
    {
        public StreamMonitorService StreamMonitorService { get; set; }
        public DiscordClient DiscordClient { get; set; }

        [Command("updatestreams")]
        [Description("Force an update of Twitch livestreams")]
        public async Task ForceUpdateStreamsAsync(CommandContext context)
        {
            StreamMonitorService.UpdateCurrentStreamersAsync(null);

            await ReplyNewEmbedAsync(context, "Updating Livestreams", MomentumColor.Blue);
        }

        [Command("forcereconnect")]
        [Description("Simulates the Discord API requesting a reconnect")]
        public async Task ForceReconnectAsync(CommandContext context, int seconds)
        {
            await DiscordClient.DisconnectAsync();
            await Task.Delay(seconds * 1000);
            await DiscordClient.ReconnectAsync();
        }
        
        [Command("forcerestart")]
        [Description("Forces the bot to exit the process, and have Docker auto-restart it")]
        public Task ForceRestartAsync(CommandContext context)
        {
            Logger.Warning("{User} forced the bot to restart", context.User);
            
            // Safe exit
            Environment.Exit(0);

            return Task.CompletedTask;
        }
    }
}