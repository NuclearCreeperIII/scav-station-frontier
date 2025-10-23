using Content.Server._NF.Radio; // Frontier
using Content.Server.Administration.Logs;
using Content.Server.Chat.Systems;
using Content.Server.Power.Components;
using Content.Server.Radio.Components;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Robust.Server.GameObjects; // Frontier
using Content.Shared.Speech;
using Content.Shared.Ghost; // Nuclear-14
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Replays;
using Robust.Shared.Utility;
using Robust.Shared.Physics;
using System.Linq;

namespace Content.Server.Radio.EntitySystems;

/// <summary>
///     This system handles intrinsic radios and the general process of converting radio messages into chat messages.
/// </summary>
public sealed class RadioSystem : EntitySystem
{
    [Dependency] private readonly INetManager _netMan = default!;
    [Dependency] private readonly IReplayRecordingManager _replay = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ChatSystem _chat = default!;

    [Dependency] private readonly SharedTransformSystem _transform = default!; //Scav

    // set used to prevent radio feedback loops.
    private readonly HashSet<string> _messages = new();

    private EntityQuery<TelecomExemptComponent> _exemptQuery;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<IntrinsicRadioReceiverComponent, RadioReceiveEvent>(OnIntrinsicReceive);
        SubscribeLocalEvent<IntrinsicRadioTransmitterComponent, EntitySpokeEvent>(OnIntrinsicSpeak);

        _exemptQuery = GetEntityQuery<TelecomExemptComponent>();
    }

    private void OnIntrinsicSpeak(EntityUid uid, IntrinsicRadioTransmitterComponent component, EntitySpokeEvent args)
    {
        if (args.Channel != null && component.Channels.Contains(args.Channel.ID))
        {
            SendRadioMessage(uid, args.Message, args.Channel, uid);
            args.Channel = null; // prevent duplicate messages from other listeners.
        }
    }

    //Nuclear-14
    /// <summary>
    /// Gets the message frequency, if there is no such frequency, returns the standard channel frequency.
    /// </summary>
    public int GetFrequency(EntityUid source, RadioChannelPrototype channel)
    {
        if (TryComp<RadioMicrophoneComponent>(source, out var radioMicrophone))
            return radioMicrophone.Frequency;

        return channel.Frequency;
    }

    private void OnIntrinsicReceive(EntityUid uid, IntrinsicRadioReceiverComponent component, ref RadioReceiveEvent args)
    {
        if (TryComp(uid, out ActorComponent? actor))
            _netMan.ServerSendMessage(args.ChatMsg, actor.PlayerSession.Channel);
    }

    /// <summary>
    /// Send radio message to all active radio listeners
    /// </summary>
    public void SendRadioMessage(EntityUid messageSource, string message, ProtoId<RadioChannelPrototype> channel, EntityUid radioSource, int? frequency = null, bool escapeMarkup = true) // Frontier: added frequency
    {
        SendRadioMessage(messageSource, message, _prototype.Index(channel), radioSource, frequency: frequency, escapeMarkup: escapeMarkup); // Frontier: added frequency
    }

    /// <summary>
    /// Send radio message to all active radio listeners
    /// </summary>
    /// <param name="messageSource">Entity that spoke the message</param>
    /// <param name="radioSource">Entity that picked up the message and will send it, e.g. headset</param>
    public void SendRadioMessage(EntityUid messageSource, string message, RadioChannelPrototype channel, EntityUid radioSource, int? frequency = null, bool escapeMarkup = true) // Nuclear-14: add frequency
    {
        // TODO if radios ever garble / modify messages, feedback-prevention needs to be handled better than this.
        var messageOriginal = message; // Scav: attempted the fix mentioned above
        if (!_messages.Add(messageOriginal))
            return;

        var evt = new TransformSpeakerNameEvent(messageSource, MetaData(messageSource).EntityName);
        RaiseLocalEvent(messageSource, evt);

        // Frontier: add name transform event
        var transformEv = new RadioTransformMessageEvent(channel, radioSource, evt.VoiceName, message, messageSource);
        RaiseLocalEvent(radioSource, ref transformEv);
        message = transformEv.Message;
        messageSource = transformEv.MessageSource;
        // End Frontier

        var name = transformEv.Name; // Frontier: evt.VoiceName<transformEv.Name
        name = FormattedMessage.EscapeText(name);

        SpeechVerbPrototype speech;
        if (evt.SpeechVerb != null && _prototype.TryIndex(evt.SpeechVerb, out var evntProto))
            speech = evntProto;
        else
            speech = _chat.GetSpeechVerb(messageSource, message);

        var content = escapeMarkup
            ? FormattedMessage.EscapeText(message)
            : message;

        // Frontier: append frequency if the channel requests it
        string channelText;
        if (channel.ShowFrequency)
            channelText = $"\\[{channel.LocalizedName} ({frequency})\\]";
        else
            channelText = $"\\[{channel.LocalizedName}\\]";
        // End Frontier

        var wrappedMessage = Loc.GetString(speech.Bold ? "chat-radio-message-wrap-bold" : "chat-radio-message-wrap",
            ("color", channel.Color),
            ("fontType", speech.FontId),
            ("fontSize", speech.FontSize),
            ("verb", Loc.GetString(_random.Pick(speech.SpeechVerbStrings))),
            ("channel", channelText), // Frontier: $"\\[{channel.LocalizedName}\\]"<channelText
            ("name", name),
            ("message", content));

        var sendAttemptEv = new RadioSendAttemptEvent(channel, radioSource);
        RaiseLocalEvent(ref sendAttemptEv);
        RaiseLocalEvent(radioSource, ref sendAttemptEv);
        var canSend = !sendAttemptEv.Cancelled;

        // Scav: using new override
        var sourceMapId = Transform(radioSource).MapID;
        var sourceTransform = Transform(radioSource);
        var sourceActiveServers = channel.Range switch
        {
            ChannelRange.ShortRange => GetActiveServers(sourceTransform, channel.ID, false),
            ChannelRange.LongRange => GetActiveServers(sourceTransform, channel.ID , true),
            _ => new List<EntityUid>()
        };
        //End Scav
        var sourceServerExempt = _exemptQuery.HasComp(radioSource);

        var radioQuery = EntityQueryEnumerator<ActiveRadioComponent, TransformComponent>();

        if (frequency == null) // Nuclear-14
            frequency = GetFrequency(messageSource, channel); // Nuclear-14

        while (canSend && radioQuery.MoveNext(out var receiver, out var radio, out var transform))
        {
            if (!radio.ReceiveAllChannels)
            {
                if (!radio.Channels.Contains(channel.ID) || (TryComp<IntercomComponent>(receiver, out var intercom) &&
                                                             !intercom.SupportedChannels.Contains(channel.ID)))
                    continue;
            }

            if (!HasComp<GhostComponent>(receiver) && GetFrequency(receiver, channel) != frequency) // Nuclear-14
                continue; // Nuclear-14

            // Scav: restructured all of this to just check channelrange == global first, because basically every step in here also checked that
            if (channel.Range != ChannelRange.Global)
            {
                if (transform.MapID != sourceMapId && !radio.GlobalReceive)
                    continue;

                // don't need telecom server for handheld radios and intercoms
                if (!sourceServerExempt && sourceActiveServers.Count == 0) //shouldnt this go before the reciever loop
                    continue;

<<<<<<< Updated upstream
                var recieverServerExempt = _exemptQuery.HasComp(receiver);
                var recieverActiveServers = GetActiveServers(transform, channel.ID, channel.Range == ChannelRange.LongRange);

                if (!recieverServerExempt && !(sourceActiveServers.Intersect(recieverActiveServers).Any()))
                    continue;
            }
            // End Scav
=======
            var recieverNeedServer = !channel.LongRange && !_exemptQuery.HasComp(receiver);

            var recieverSignalDegradation = GetLowestDegradation(sourceActiveServers, transform, sourceTransform); //Of all the servers available to both sender and reciever, get the lowest possible signal degradation

            if (recieverNeedServer && recieverSignalDegradation == 1)
                continue;
>>>>>>> Stashed changes

            //message = ApplyMessageDegradation(message, recieverSignalDegradation);
            message += recieverSignalDegradation.ToString();
            message += "demo";

            // check if message can be sent to specific receiver
            var attemptEv = new RadioReceiveAttemptEvent(channel, radioSource, receiver);
            RaiseLocalEvent(ref attemptEv);
            RaiseLocalEvent(receiver, ref attemptEv);
            if (attemptEv.Cancelled)
                continue;


            // Scav: the chat message generation needs to happen here because of degradation
            var chat = new ChatMessage(
                ChatChannel.Radio,
                message,
                wrappedMessage,
                NetEntity.Invalid,
                null);
            var chatMsg = new MsgChatMessage { Message = chat };
            var ev = new RadioReceiveEvent(message, messageSource, channel, radioSource, chatMsg);

            // send the message
            RaiseLocalEvent(receiver, ref ev);
        }

        if (name != Name(messageSource))
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Radio message from {ToPrettyString(messageSource):user} as {name} on {channel.LocalizedName}: {message}");
        else
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Radio message from {ToPrettyString(messageSource):user} on {channel.LocalizedName}: {message}");

        // most radios are relayed to chat, so lets parse the chat message beforehand
        var chatUndoctored = new ChatMessage(
            ChatChannel.Radio,
            messageOriginal,
            wrappedMessage,
            NetEntity.Invalid,
            null);
        _replay.RecordServerMessage(chatUndoctored);
        _messages.Remove(messageOriginal);
    }

    /// <inheritdoc cref="TelecomServerComponent"/>
    private bool HasActiveServer(MapId mapId, string channelId)
    {
        var servers = EntityQuery<TelecomServerComponent, EncryptionKeyHolderComponent, ApcPowerReceiverComponent, TransformComponent>();
        foreach (var (_, keys, power, transform) in servers)
        {
            if (transform.MapID == mapId &&
                power.Powered &&
                keys.Channels.Contains(channelId))
            {
                return true;
            }
        }
        return false;
    }

    //Scav: new helper functions
    private bool HasActiveServer(TransformComponent radioTransform, string channelId) //long term, non long-range channels should be filtered both by available encryption keys, AND the uid of the servers in range (possibly as a list of uids). this will likely require this funcitonality to move
    {
        var servers = EntityQuery<TelecomServerComponent, EncryptionKeyHolderComponent, ApcPowerReceiverComponent, TransformComponent>();
        foreach (var (server, keys, power, serverTransform) in servers)
        {
            if (serverTransform.MapID == radioTransform.MapID &&
                (_transform.GetMapCoordinates(radioTransform).Position - _transform.GetMapCoordinates(serverTransform).Position).Length() <= server.outerRange &&
                power.Powered &&
                keys.Channels.Contains(channelId))
            {
                return true;
            }
        }
        return false;
    }

<<<<<<< Updated upstream
    private List<EntityUid> GetActiveServers(TransformComponent radioTransform, string channelId, bool longRange = false) //may want to make this a list of <telecomservercomponent, transformcomponent> instead of uid. we dont actually need the uids if we dont call this for both sender and reciever and just iterate over what the sender found
=======
    private List<(TelecomServerComponent, TransformComponent)> GetActiveServers(TransformComponent radioTransform, string channelId)
>>>>>>> Stashed changes
    {
        List<(TelecomServerComponent, TransformComponent)> activeServers = new List<(TelecomServerComponent, TransformComponent)>();

        var servers = EntityQuery<TelecomServerComponent, EncryptionKeyHolderComponent, ApcPowerReceiverComponent, TransformComponent>();
        foreach (var (server, keys, power, serverTransform) in servers)
        {
            if (serverTransform.MapID == radioTransform.MapID &&
<<<<<<< Updated upstream
                (longRange || (_transform.GetMapCoordinates(radioTransform).Position - _transform.GetMapCoordinates(serverTransform).Position).Length() <= server.range) &&
=======
                (_transform.GetMapCoordinates(radioTransform).Position - _transform.GetMapCoordinates(serverTransform).Position).Length() <= server.outerRange &&
>>>>>>> Stashed changes
                power.Powered &&
                keys.Channels.Contains(channelId))
            {
                activeServers.Add((server, serverTransform));
            }
        }
        return activeServers;
    }

    private float GetLowestDegradation(List<(TelecomServerComponent, TransformComponent)> filterList, TransformComponent recieverTransform, TransformComponent senderTransform)
    {
        List<(TelecomServerComponent, TransformComponent)> activeServers = new List<(TelecomServerComponent, TransformComponent)>();

        float lowestDegradation = 1; //1 is full degradation, we will assume this unless otherwise noted

        float degradation = 0;

        float recieverDistance;
        float senderDistance;

        foreach (var (server, serverTransform) in filterList)
        {
            recieverDistance = (_transform.GetMapCoordinates(recieverTransform).Position - _transform.GetMapCoordinates(serverTransform).Position).Length();
            if (recieverDistance <= server.outerRange) //We only care about servers shared by both sender and reciever, others will send no signal at all
            {
                senderDistance = (_transform.GetMapCoordinates(senderTransform).Position - _transform.GetMapCoordinates(serverTransform).Position).Length();

                degradation = (Math.Clamp((recieverDistance - server.innerRange) / (server.outerRange - server.innerRange), 0, 1)
                               + Math.Clamp((senderDistance - server.innerRange) / (server.outerRange - server.innerRange), 0, 1)
                               / 2); //we want a range from 0 to 1 but it should take both distances into account.

                if (degradation < lowestDegradation)
                {
                    lowestDegradation = degradation;
                }
            }
        }

        return lowestDegradation;
    }

    private string ApplyMessageDegradation(string message, float degradation)
    {
        char[] messageArray = message.ToCharArray();
        Random rand = new Random();
        for (int i = 0; i < messageArray.Length; i++)
        {
            if (rand.Next(0, 10) < degradation * 10)
            {
                messageArray[i] = '-';
            }
        }

        return new string(messageArray);
    }
    // End Scav
}
