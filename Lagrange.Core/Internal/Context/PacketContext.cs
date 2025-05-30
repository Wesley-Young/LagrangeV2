using System.Collections.Concurrent;
using Lagrange.Core.Common;
using Lagrange.Core.Internal.Packets.Struct;
using Lagrange.Core.Internal.Services;

namespace Lagrange.Core.Internal.Context;

internal class PacketContext(BotContext context)
{
    private readonly ConcurrentDictionary<int, SsoPacketValueTaskSource> _pendingTasks = new();
    
    private readonly BotKeystore _keystore = context.Keystore;
    private readonly SsoPacker _ssoPacker = new(context);
    private readonly ServicePacker _servicePacker = new(context);
    
    internal readonly IBotSignProvider SignProvider = context.Config.SignProvider ?? context.Config.Protocol switch
    {
        Protocols.Linux or Protocols.Windows or Protocols.MacOs => new DefaultBotSignProvider(context),
        Protocols.AndroidPhone or Protocols.AndroidPad => new DefaultAndroidBotSignProvider(context),
        _ => throw new ArgumentOutOfRangeException(nameof(context.Config.Protocol))
    };

    public ValueTask<SsoPacket> SendPacket(SsoPacket packet, ServiceAttribute options)
    {
        var tcs = new SsoPacketValueTaskSource();
        _pendingTasks.TryAdd(packet.Sequence, tcs);

        Task.Run(async () => // Schedule the task to the ThreadPool
        {
            ReadOnlyMemory<byte> frame;
            
            switch (options.RequestType)
            {
                case RequestType.D2Auth:
                {
                    if (SignProvider.IsWhiteListCommand(packet.Command))
                    {
                        var secInfo = await SignProvider.GetSecSign(_keystore.Uin, packet.Command, packet.Sequence, packet.Data);
                        var sso = _ssoPacker.BuildProtocol12(packet, secInfo);
                        frame = _servicePacker.BuildProtocol12(sso, options);
                    }
                    else
                    {
                        var sso = _ssoPacker.BuildProtocol12(packet, null);
                        frame = _servicePacker.BuildProtocol12(sso, options);
                    }

                    break;
                }
                case RequestType.Simple:
                {
                    var sso = _ssoPacker.BuildProtocol13(packet);
                    frame = _servicePacker.BuildProtocol13(packet, sso, options);
                    break;
                }
                default:
                {
                    throw new InvalidOperationException($"Unknown RequestType: {options.RequestType}");
                }
            }
            
            await context.SocketContext.Send(frame);
        });
        
        return new ValueTask<SsoPacket>(tcs, 0);
    }

    public void DispatchPacket(ReadOnlySpan<byte> buffer)
    {
        var service = _servicePacker.Parse(buffer);
        var sso = _ssoPacker.Parse(service);
        
        if (_pendingTasks.TryRemove(sso.Sequence, out var tcs))
        {
            if (sso is { RetCode: not 0, Extra: var extra })
            {
                string msg = $"Packet '{sso.Command}' returns {sso.RetCode} with seq: {sso.Sequence}, extra: {extra}";
                tcs.SetException(new InvalidOperationException(msg));
            }
            else
            {
                tcs.SetResult(sso);
            }
        }
        else
        {
            _ = context.EventContext.HandleServerPacket(sso);
        }
    }
}