﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using MomentumDiscordBot.Models;
using MomentumDiscordBot.Utilities;
using TwitchLib.Api.Helix.Models.Streams;

namespace MomentumDiscordBot.Services
{
    /// <summary>
    ///     Service to provide a list of current streamers playing Momentum Mod.
    /// </summary>
    public class StreamMonitorService
    {
        private readonly ulong _channelId;

        private readonly TimeSpan _updateInterval;

        // <StreamID, MessageID>
        private Dictionary<string, ulong> _cachedStreamsIds;
        private readonly Config _config;
        private readonly DiscordSocketClient _discordClient;
        private SocketTextChannel _textChannel;
        public readonly TwitchApiService TwitchApiService;
        private Timer _intervalFunctionTimer;
        private readonly List<string> _streamSoftBanList = new List<string>();
        private readonly LogService _logger;
        private readonly SemaphoreSlim semaphoreSlimLock = new SemaphoreSlim(1, 1);

        public StreamMonitorService(DiscordSocketClient discordClient, Config config, LogService logger)
        {
            _config = config;
            _discordClient = discordClient;
            _logger = logger;

            TwitchApiService = new TwitchApiService(_logger);

            _channelId = _config.MomentumModStreamerChannelId;
            _updateInterval = TimeSpan.FromMinutes(_config.StreamUpdateInterval);
            _discordClient.Ready += _discordClient_Ready;
        }

        private Task _discordClient_Ready()
        {
            _ = Task.Run(async () =>
            {
                _textChannel = _discordClient.GetChannel(_channelId) as SocketTextChannel;

                await TryParseExistingEmbedsAsync();

                _intervalFunctionTimer = new Timer(UpdateCurrentStreamersAsync, null, TimeSpan.Zero, _updateInterval);
            });

            return Task.CompletedTask;
        }

        public async void UpdateCurrentStreamersAsync(object state)
        {
            // Wait for the semaphore to unlock, then lock it
            await semaphoreSlimLock.WaitAsync();

            var streams = await TwitchApiService.GetLiveMomentumModStreamersAsync();

            // On error no need to continue
            if (streams == null)
            {
                semaphoreSlimLock.Release();
                return;
            }

            await DeleteBannedStreamsAsync(streams);
            await UnSoftbanEndedStreamsAsync(streams);
            await RegisterSoftBansAsync();

            TwitchApiService.PreviousLivestreams = streams;

            // Filter out soft/hard banned streams
            var filteredStreams = streams.Where(x => !IsSoftBanned(x) && !IsHardBanned(x)).ToList();

            // Reload embeds
            try
            {
                // If there is an exception when parsing the existing embeds, no need to continue
                // Return early when there are no streams as well, as no need to send/update
                if (!await TryParseExistingEmbedsAsync() || filteredStreams.Count == 0)
                {
                    semaphoreSlimLock.Release();
                    return;
                }

                await SendOrUpdateStreamEmbedsAsync(filteredStreams);
            }
            catch (Exception e)
            {
                _ = _logger.LogError("StreamMonitorService", e.ToString());
            }

            semaphoreSlimLock.Release();
        }

        private async Task SendOrUpdateStreamEmbedsAsync(List<Stream> filteredStreams)
        {
            foreach (var stream in filteredStreams)
            {
                var (embed, messageText) = await GetStreamEmbed(stream);

                // New streams are not in the cache
                if (!IsStreamInCache(stream))
                {
                    // If the stream is not above the minimum viewers then ignore it, but we want to update a stream if it dips below
                    if (stream.ViewerCount < _config.MinimumStreamViewersAnnounce) continue;

                    // New stream, send a new message
                    var message =
                        await _textChannel.SendMessageAsync(messageText, embed: embed);

                    _cachedStreamsIds.Add(stream.Id, message.Id);
                }
                else
                {
                    // Get the message id from the stream
                    if (!_cachedStreamsIds.TryGetValue(stream.Id, out var messageId))
                    {
                        _ = _logger.LogWarning("StreamMonitorService", "Could not message from cached stream ID");
                        continue;
                    }

                    // Existing stream, update message with new information
                    var oldMessage = await _textChannel.GetMessageAsync(messageId);
                    if (oldMessage is IUserMessage oldRestMessage)
                        await oldRestMessage.ModifyAsync(x =>
                        {
                            x.Content = messageText;
                            x.Embed = embed;
                        });
                }
            }
        }

        private bool IsStreamInCache(Stream stream) => _cachedStreamsIds.ContainsKey(stream.Id);

        private async Task<KeyValuePair<Embed, string>> GetStreamEmbed(Stream stream)
        {
            var messageText =
                $"{stream.UserName.EscapeDiscordChars()} has gone live! {MentionUtils.MentionRole(_config.LivestreamMentionRoleId)}";

            var embed = new EmbedBuilder
            {
                Title = stream.Title.EscapeDiscordChars(),
                Color = Color.Purple,
                Author = new EmbedAuthorBuilder
                {
                    Name = stream.UserName,
                    IconUrl = await TwitchApiService.GetStreamerIconUrlAsync(stream.UserId),
                    Url = $"https://twitch.tv/{stream.UserName}"
                },
                ImageUrl = stream.ThumbnailUrl.Replace("{width}", "1280").Replace("{height}", "720") + "?q=" +
                           Environment.TickCount,
                Description = stream.ViewerCount + " viewers",
                Url = $"https://twitch.tv/{stream.UserName}",
                Timestamp = DateTimeOffset.Now
            }.Build();

            return new KeyValuePair<Embed, string>(embed, messageText);
        }

        private bool IsHardBanned(Stream stream) => (_config.TwitchUserBans ?? new string[0]).Contains(stream.UserId);

        private bool IsSoftBanned(Stream stream) => _streamSoftBanList.Contains(stream.Id);

        private async Task RegisterSoftBansAsync()
        {
            // Check for soft-banned stream, when a mod deletes the message
            try
            {
                var existingSelfMessages =
                    (await _textChannel.GetMessagesAsync(200).FlattenAsync()).FromSelf(_discordClient);
                var softBannedMessages = _cachedStreamsIds.Where(x => existingSelfMessages.All(y => y.Id != x.Value));
                _streamSoftBanList.AddRange(softBannedMessages.Select(x => x.Key));
            }
            catch (Exception e)
            {
                _ = _logger.LogWarning("StreamMonitorService", e.Message);
            }
        }

        private async Task UnSoftbanEndedStreamsAsync(IEnumerable<Stream> streams)
        {
            // If the cached stream id's isn't in the fetched stream id, it is an ended stream
            var streamIds = streams.Select(x => x.Id);
            var endedStreams = _cachedStreamsIds.Where(x => !streamIds.Contains(x.Key));

            foreach (var (endedStreamId, messageId) in endedStreams)
            {
                // If the stream was soft banned, remove it
                if (_streamSoftBanList.Contains(endedStreamId)) _streamSoftBanList.Remove(endedStreamId);

                try
                {
                    await _textChannel.DeleteMessageAsync(messageId);
                }
                catch
                {
                    _ = _logger.LogWarning("StreamMonitorService",
                        "Tried to delete message " + messageId + " but it does not exist.");
                }

                _cachedStreamsIds.Remove(endedStreamId);
            }
        }

        private async Task DeleteBannedStreamsAsync(IEnumerable<Stream> streams)
        {
            // Get streams from banned users
            if (_config.TwitchUserBans != null && _config.TwitchUserBans.Length > 0)
            {
                var bannedStreams = streams.Where(x => _config.TwitchUserBans.Contains(x.UserId));

                foreach (var bannedStream in bannedStreams) 
                {
                    if (_cachedStreamsIds.TryGetValue(bannedStream.Id, out var messageId))
                    {
                        await _textChannel.DeleteMessageAsync(messageId);
                    }
                }
                    
            }
        }

        private async Task<bool> TryParseExistingEmbedsAsync()
        {
            // Reset cache
            _cachedStreamsIds = new Dictionary<string, ulong>();

            // Get all messages
            var messages = (await _textChannel.GetMessagesAsync().FlattenAsync()).FromSelf(_discordClient).ToList();

            if (!messages.Any()) return true;

            var streams = await TwitchApiService.GetLiveMomentumModStreamersAsync();

            // Error getting streams, don't continue
            if (streams == null) return false;

            // Delete existing bot messages simultaneously
            var deleteTasks = messages
                .Select(async x =>
                {
                    if (x.Embeds.Count == 1)
                    {
                        var matchingStream = streams.FirstOrDefault(y => y.UserName == x.Embeds.First().Author?.Name);
                        if (matchingStream == null)
                        {
                            // No matching stream
                            await x.DeleteAsync();
                        }
                        else
                        {
                            // Found the matching stream
                            if (!_cachedStreamsIds.TryAdd(matchingStream.Id, x.Id))
                            {
                                await _logger.LogWarning("StreamMonitorService",
                                    "Duplicate cached streamer: " + matchingStream.UserName + ", deleting...");
                                await x.DeleteAsync();
                            }
                        }
                    }
                    else
                    {
                        // Stream has ended, or failed to parse
                        await x.DeleteAsync();
                    }
                });
            await Task.WhenAll(deleteTasks);
            return true;
        }
    }
}