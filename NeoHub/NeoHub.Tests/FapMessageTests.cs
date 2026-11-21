using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.MediatR;
using DSC.TLink.ITv2.Messages;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NeoHub.Tests.Infrastructure;

namespace NeoHub.Tests;

/// <summary>The ModuleControl_Activate_FAP message, its absence from the Dev Console catalog, and the SessionMediator gate.</summary>
public class FapMessageTests
{
    private const ushort FapCommandWord = 0x0908;

    [Theory]
    [InlineData(1, FapType.Fire, new byte[] { 0x09, 0x08, 0x21, 0x01, 0x01, 0x00 })]
    [InlineData(1, FapType.Auxiliary, new byte[] { 0x09, 0x08, 0x21, 0x01, 0x01, 0x01 })]
    [InlineData(1, FapType.Panic, new byte[] { 0x09, 0x08, 0x21, 0x01, 0x01, 0x02 })]
    [InlineData(2, FapType.Fire, new byte[] { 0x09, 0x08, 0x21, 0x01, 0x02, 0x00 })]
    public void ActivateFap_encodes_sequence_compact_partition_and_type(int partition, FapType type, byte[] expected)
    {
        var message = new ActivateFap { CommandSequence = 0x21, Partition = partition, Type = type };
        Assert.Equal(expected, MessageFactory.SerializeMessage(message).ToArray());
    }

    [Fact]
    public void ActivateFap_round_trips()
    {
        var bytes = MessageFactory.SerializeMessage(new ActivateFap { CommandSequence = 5, Partition = 1, Type = FapType.Panic }).ToArray();
        var decoded = Assert.IsType<ActivateFap>(MessageFactory.DeserializeMessage(bytes));
        Assert.Equal((5, 1, FapType.Panic), (decoded.CommandSequence, decoded.Partition, decoded.Type));
    }

    [Fact]
    public void The_dev_console_catalog_offers_no_FAP_command()
    {
        var sendable = MessageCatalog.GetSendable();
        Assert.NotEmpty(sendable);
        Assert.DoesNotContain(sendable, m => m.Command == ITv2Command.ModuleControl_Activate_FAP || m.MessageType == typeof(ActivateFap));

        // Whatever the Dev Console builds from the catalog (a default instance of a listed type) is not a FAP command.
        foreach (var info in sendable)
        {
            var instance = (IMessageData)Activator.CreateInstance(info.MessageType)!;
            Assert.False(SessionMediator.CarriesFap(instance), info.DisplayName);
            Assert.NotEqual(ITv2Command.ModuleControl_Activate_FAP, MessageFactory.GetCommand(instance));
        }
        Assert.Contains(MessageCatalog.GetAll(), m => m.MessageType == typeof(ActivateFap));   // registered for decoding
    }

    [Fact]
    public void CarriesFap_sees_wrapped_FAP_messages()
    {
        var fap = new ActivateFap { Partition = 1, Type = FapType.Fire };
        Assert.True(SessionMediator.CarriesFap(fap));
        Assert.True(SessionMediator.CarriesFap(new CommandRequestMessage { Request = fap }));
        Assert.True(SessionMediator.CarriesFap(new MultipleMessagePacket { Messages = new IMessageData[] { new ConnectionPoll(), fap } }));
        Assert.True(SessionMediator.CarriesFap(new DefaultMessage { Command = ITv2Command.ModuleControl_Activate_FAP }));
        Assert.False(SessionMediator.CarriesFap(new PartitionArm { Partition = 1 }));
        Assert.False(SessionMediator.CarriesFap(new CommandRequestMessage { Request = new ModulePartitionStatus { Partition = 1 } }));
    }

    private sealed class Policy : IFapCommandPolicy
    {
        public bool AllowFapCommands { get; set; }
    }

    private static (SessionMediator Mediator, ITv2SessionManager Manager) MediatorFor(IITv2Session session, params IFapCommandPolicy[] policies)
    {
        var manager = new ITv2SessionManager(NullProxy.Create<IMediator>(), NullLogger<ITv2SessionManager>.Instance);
        manager.RegisterSession(session.SessionId, session);
        var mediator = policies.Length == 0
            ? new SessionMediator(NullProxy.Create<IMediator>(), manager, NullLogger<SessionMediator>.Instance)
            : new SessionMediator(NullProxy.Create<IMediator>(), manager, NullLogger<SessionMediator>.Instance, policies);
        return (mediator, manager);
    }

    private static int FapFrames(RealSession s) => s.Transport.SentPayloads.Count(p => FakeTransport.CarriesCommandWord(p, FapCommandWord));

    [Fact]
    public async Task Without_a_policy_or_with_FAP_off_nothing_leaves_the_transport()
    {
        var s = await RealSession.StartAsync("S1");
        var (noPolicy, _) = MediatorFor(s.Session);
        var off = new Policy { AllowFapCommands = false };
        var (withOff, _) = MediatorFor(s.Session, off);

        foreach (var mediator in new[] { noPolicy, withOff })
        {
            foreach (var message in new IMessageData[]
                     {
                         new ActivateFap { Partition = 1, Type = FapType.Fire },
                         new CommandRequestMessage { Request = new ActivateFap { Partition = 1, Type = FapType.Panic } },
                         new MultipleMessagePacket { Messages = new IMessageData[] { new ActivateFap { Partition = 1, Type = FapType.Auxiliary } } },
                     })
            {
                var response = await mediator.Handle(new SessionCommand { SessionID = "S1", MessageData = message }, CancellationToken.None);
                Assert.False(response.Success);
                Assert.True(response.NotSent);
                Assert.Equal("FAP commands are disabled in the NeoHub settings", response.ErrorMessage);
            }
        }

        Assert.Equal(0, FapFrames(s));
        await s.Session.DisposeAsync();
    }

    [Fact]
    public async Task With_FAP_allowed_the_command_leaves_once_with_the_expected_bytes()
    {
        var s = await RealSession.StartAsync("S1");
        var (mediator, _) = MediatorFor(s.Session, new Policy { AllowFapCommands = true });
        s.Transport.OnSend = packet =>
        {
            if (packet.Message is ActivateFap fap)
                s.Transport.Feed(packet.SenderSequence, new CommandResponse { CommandSequence = fap.CommandSequence, ResponseCode = CommandResponseCode.Success });
        };

        var response = await mediator.Handle(new SessionCommand
        {
            SessionID = "S1",
            MessageData = new ActivateFap { Partition = 1, Type = FapType.Fire }
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(response.Success);
        var frame = Assert.Single(s.Transport.SentPayloads, p => FakeTransport.CarriesCommandWord(p, FapCommandWord));
        var sent = Assert.IsType<ActivateFap>(FakeTransport.Decode(frame).Message);
        Assert.Equal((1, FapType.Fire), (sent.Partition, sent.Type));
        var body = MessageFactory.SerializeMessage(sent).ToArray();
        Assert.Equal(new byte[] { 0x09, 0x08, sent.CommandSequence, 0x01, 0x01, 0x00 }, body);
        await s.Session.DisposeAsync();
    }
}
